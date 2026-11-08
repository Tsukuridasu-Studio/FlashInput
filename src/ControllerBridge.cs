using System;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace FlashInput
{
    /// <summary>
    /// The translation layer. Reads the picked DragonRise-style pad through winmm and
    /// mirrors it onto a virtual Xbox 360 pad (ViGEm) so games see a normal XInput controller.
    /// </summary>
    public sealed class ControllerBridge : IDisposable
    {
        // ---- button mapping, tweak this to match your pad ----
        // index = physical button number on the generic pad (0 = "button 1" in windows' game controller panel)
        // most dragonrise pads are laid out roughly like this, but not all of them
        private static readonly Xbox360Button?[] ButtonMap =
        {
            Xbox360Button.A,             // 0
            Xbox360Button.B,             // 1
            Xbox360Button.X,             // 2
            Xbox360Button.Y,             // 3
            Xbox360Button.LeftShoulder,  // 4
            Xbox360Button.RightShoulder, // 5
            null,                        // 6  -> left trigger (see below)
            null,                        // 7  -> right trigger
            Xbox360Button.Back,          // 8
            Xbox360Button.Start,         // 9
            Xbox360Button.LeftThumb,     // 10
            Xbox360Button.RightThumb     // 11
        };

        // cheap pads have digital triggers, so a button press = full trigger pull
        private const int LeftTriggerButton = 6;
        private const int RightTriggerButton = 7;

        private readonly object _gate = new();

        private ViGEmClient? _client;
        private IXbox360Controller? _pad;
        private Thread? _worker;
        private CancellationTokenSource? _cts;
        private JoystickInfo? _selected;
        private bool _disposed;

        /// <summary>Raised on the UI thread if the pad vanishes or the worker dies. Call Stop() in response.</summary>
        public event EventHandler<string>? Faulted;

        public JoystickInfo? SelectedDevice
        {
            get { lock (_gate) return _selected; }
            set { lock (_gate) _selected = value; }
        }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return !_disposed && _worker is { IsAlive: true };
            }
        }

        /// <summary>Boots the virtual pad and the polling thread. No-op if already running.</summary>
        public void Start()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_worker is { IsAlive: true }) return;

                // a worker that died on its own (pad unplugged) still has stuff to free
                TearDown();

                if (_selected is null)
                    throw new InvalidOperationException("Pick a controller first (Select Controller in the tray menu).");

                ViGEmClient? client = null;
                IXbox360Controller? pad = null;
                CancellationTokenSource? cts = null;
                bool handedOff = false;

                try
                {
                    // throws if the ViGEmBus driver isn't installed
                    client = new ViGEmClient();
                    pad = client.CreateXbox360Controller();
                    pad.AutoSubmitReport = false; // we submit once per frame ourselves
                    pad.Connect();

                    cts = new CancellationTokenSource();
                    var token = cts.Token;
                    uint deviceId = _selected.Id;
                    var capturedPad = pad;
                    // grab the ui context BEFORE the thread starts, so it can never see a null one
                    var ui = SynchronizationContext.Current;

                    var worker = new Thread(() => PollLoop(deviceId, capturedPad, token, ui))
                    {
                        IsBackground = true,
                        Name = "FlashInput translation"
                    };
                    worker.Start();

                    _client = client;
                    _pad = pad;
                    _cts = cts;
                    _worker = worker;
                    handedOff = true;
                }
                finally
                {
                    // only runs for real if something above threw before we took ownership
                    if (!handedOff)
                        Release(pad, client, cts);
                }
            }
        }

        /// <summary>Stops translating and frees everything. Safe to call over and over.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                TearDown();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                TearDown();
            }
        }

        // order: tell the thread to stop -> wait for it -> unplug the pad -> free the client
        private void TearDown()
        {
            var worker = _worker;
            var cts = _cts;
            var pad = _pad;
            var client = _client;

            _worker = null;
            _cts = null;
            _pad = null;
            _client = null;

            try { cts?.Cancel(); } catch { /* already disposed, whatever */ }

            // the loop only sleeps ~2ms at a time so this returns almost instantly.
            // we MUST wait here, freeing the pad while the thread still uses it would crash
            worker?.Join();

            Release(pad, client, cts);
        }

        private static void Release(IXbox360Controller? pad, ViGEmClient? client, CancellationTokenSource? cts)
        {
            if (pad is not null)
            {
                try { pad.Disconnect(); } catch { /* never got connected, fine */ }
                (pad as IDisposable)?.Dispose();
            }

            client?.Dispose();
            cts?.Dispose();
        }

        // ------------------------------------------------------------
        // the actual translation loop, runs on its own thread
        // ------------------------------------------------------------

        private void PollLoop(uint deviceId, IXbox360Controller pad, CancellationToken token, SynchronizationContext? ui)
        {
            WinMmJoystick.BeginHighResTimer();
            try
            {
                JoystickState? last = null;

                while (!token.IsCancellationRequested)
                {
                    if (!WinMmJoystick.TryRead(deviceId, out var state))
                    {
                        // pad got unplugged
                        RaiseFaulted(ui, "Controller disconnected.");
                        return;
                    }

                    // only talk to the driver when something actually changed
                    if (last is null || !state.Equals(last.Value))
                    {
                        Translate(state, pad);
                        pad.SubmitReport();
                        last = state;
                    }

                    // wakes up early if Stop() cancels the token
                    token.WaitHandle.WaitOne(2);
                }
            }
            catch (Exception ex)
            {
                RaiseFaulted(ui, $"Translation stopped: {ex.Message}");
            }
            finally
            {
                // leave the virtual pad neutral so games don't see a stuck stick
                try
                {
                    pad.ResetReport();
                    pad.SubmitReport();
                }
                catch { /* pad may already be gone */ }

                // paired with the Begin above, always
                WinMmJoystick.EndHighResTimer();
            }
        }

        private static void Translate(in JoystickState s, IXbox360Controller pad)
        {
            // sticks. windows y axis goes down, xbox goes up, so y is flipped
            pad.SetAxisValue(Xbox360Axis.LeftThumbX, ToStick(s.X, invert: false));
            pad.SetAxisValue(Xbox360Axis.LeftThumbY, ToStick(s.Y, invert: true));
            pad.SetAxisValue(Xbox360Axis.RightThumbX, ToStick(s.Z, invert: false));
            pad.SetAxisValue(Xbox360Axis.RightThumbY, ToStick(s.R, invert: true));

            // face buttons, shoulders, etc
            for (int i = 0; i < ButtonMap.Length; i++)
            {
                var button = ButtonMap[i];
                if (button is null) continue;
                pad.SetButtonState(button, (s.Buttons & (1u << i)) != 0);
            }

            // digital triggers
            pad.SetSliderValue(Xbox360Slider.LeftTrigger, IsDown(s.Buttons, LeftTriggerButton) ? (byte)255 : (byte)0);
            pad.SetSliderValue(Xbox360Slider.RightTrigger, IsDown(s.Buttons, RightTriggerButton) ? (byte)255 : (byte)0);

            // d-pad from the hat switch. value is angle*100, anything above 36000 means centred
            uint pov = s.Pov;
            bool hat = pov <= 36000;
            pad.SetButtonState(Xbox360Button.Up, hat && (pov <= 4500 || pov >= 31500));
            pad.SetButtonState(Xbox360Button.Right, hat && pov >= 4500 && pov <= 13500);
            pad.SetButtonState(Xbox360Button.Down, hat && pov >= 13500 && pov <= 22500);
            pad.SetButtonState(Xbox360Button.Left, hat && pov >= 22500 && pov <= 31500);
        }

        private static bool IsDown(uint buttons, int index) => (buttons & (1u << index)) != 0;

        // 0..65535 (centre ~32767) -> -32768..32767
        private static short ToStick(uint raw, bool invert)
        {
            int v = (int)raw - 32768;
            if (invert) v = -v;
            return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }

        private void RaiseFaulted(SynchronizationContext? ui, string message)
        {
            // hop back to the UI thread, never touch the tray from this worker thread
            ui?.Post(_ => Faulted?.Invoke(this, message), null);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ControllerBridge));
        }
    }
}