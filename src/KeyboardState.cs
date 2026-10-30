using System.Collections.Generic;

namespace KeySwap
{
    internal struct ScanEvent
    {
        public ushort Scan;
        public bool Up;
        public ScanEvent(ushort scan, bool up) { Scan = scan; Up = up; }
    }

    internal sealed class KeyAction
    {
        public readonly bool Handled;
        public readonly ScanEvent[] Events;
        public KeyAction(bool handled, ScanEvent[] events) { Handled = handled; Events = events; }
    }

    // Tracks physical ownership separately from the keys we have injected.
    // Called only by the keyboard worker thread, never by the UI thread.
    internal sealed class KeyboardState
    {
        private bool leftAlt, leftCtrl, switching, outputAlt, outputCtrl, outputTab;
        private bool recoveryAlt, recoveryCtrl, recoveryTab, waitingForRelease;
        private bool inputSwapped;
        public KeyboardState() { }
        public KeyboardState(bool initialAlt, bool initialCtrl)
        {
            leftAlt = initialAlt; leftCtrl = initialCtrl;
            waitingForRelease = initialAlt || initialCtrl;
        }
        public KeyboardState(bool initialAlt, bool initialCtrl, bool swapped)
            : this(initialAlt, initialCtrl) { inputSwapped = swapped; }
        public void Confirm() { recoveryAlt = recoveryCtrl = recoveryTab = false; }

        public KeyAction Handle(uint vk, uint scan, uint flags, bool up, bool preserve)
        {
            bool extended = (flags & 1) != 0;
            bool alt = !extended && (scan == 0x38 || vk == 0xA4);
            bool ctrl = !extended && (scan == 0x1D || vk == 0xA2);
            bool tab = !extended && (scan == 15 || vk == 9);
            var events = new List<ScanEvent>();
            if (waitingForRelease)
            {
                if (alt) leftAlt = !up;
                if (ctrl) leftCtrl = !up;
                waitingForRelease = leftAlt || leftCtrl;
                return new KeyAction(false, events.ToArray());
            }
            // Driver/firmware mapping affects device events, but SendInput does
            // not traverse that input path. Normalize only the calibrated source.
            if (inputSwapped && (flags & 0x10) == 0)
            {
                bool sourceAlt = alt;
                alt = ctrl;
                ctrl = sourceAlt;
            }
            // Keep prior ownership until the OS confirms the complete batch.
            recoveryAlt = outputAlt; recoveryCtrl = outputCtrl; recoveryTab = outputTab;
            if (alt || ctrl)
            {
                if (up && ((alt && !leftAlt) || (ctrl && !leftCtrl)))
                    return new KeyAction(false, events.ToArray());
                if (alt) { leftAlt = !up; if (up) switching = false; }
                else leftCtrl = !up;
                UpdateModifiers(events);
                return new KeyAction(true, events.ToArray());
            }
            if (tab && !up && leftAlt && (preserve || switching))
            {
                switching = true;
                UpdateModifiers(events);
                outputTab = true;
                events.Add(new ScanEvent(15, false));
                return new KeyAction(true, events.ToArray());
            }
            // Pair our Tab-down with our Tab-up, even if Alt was released first.
            if (tab && up && outputTab)
            {
                outputTab = false;
                events.Add(new ScanEvent(15, true));
                return new KeyAction(true, events.ToArray());
            }
            return new KeyAction(false, events.ToArray());
        }

        private void UpdateModifiers(List<ScanEvent> events)
        {
            bool wantCtrl = leftAlt && !switching;
            bool wantAlt = leftCtrl || (leftAlt && switching);
            // Release before pressing: never accidentally create Ctrl+Alt+Tab.
            if (outputCtrl && !wantCtrl) events.Add(new ScanEvent(0x1D, true));
            if (outputAlt && !wantAlt) events.Add(new ScanEvent(0x38, true));
            if (!outputCtrl && wantCtrl) events.Add(new ScanEvent(0x1D, false));
            if (!outputAlt && wantAlt) events.Add(new ScanEvent(0x38, false));
            outputCtrl = wantCtrl;
            outputAlt = wantAlt;
        }

        public KeyAction ReleaseAll()
        {
            var events = new List<ScanEvent>();
            if (outputTab || recoveryTab) events.Add(new ScanEvent(15, true));
            if (outputCtrl || recoveryCtrl) events.Add(new ScanEvent(0x1D, true));
            if (outputAlt || recoveryAlt) events.Add(new ScanEvent(0x38, true));
            leftAlt = leftCtrl = switching = outputAlt = outputCtrl = outputTab = false;
            waitingForRelease = false;
            Confirm();
            return new KeyAction(false, events.ToArray());
        }
    }
}
