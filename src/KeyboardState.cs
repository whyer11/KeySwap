using System.Collections.Generic;

namespace KeySwap
{
    internal struct ScanEvent
    {
        public ushort Scan;
        public bool Up;
        public bool Extended;
        public ScanEvent(ushort scan, bool up, bool extended = false) { Scan = scan; Up = up; Extended = extended; }
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
        private bool swapEnabled = true;
        private bool directTabDown, bridgeF12Down;
        private bool rawF12Down, rawScrollDown, bridgeScrollDown;
        private bool rightAltDown, outputRightAlt, recoveryRightAlt;
        private bool nativeWinDown, outputWin, recoveryWin;
        public KeyboardState() { }
        public KeyboardState(bool initialAlt, bool initialCtrl)
        {
            leftAlt = initialAlt; leftCtrl = initialCtrl;
            waitingForRelease = initialAlt || initialCtrl;
        }
        public KeyboardState(bool initialAlt, bool initialCtrl, bool swapped, bool swapEnabled = true,
            bool initialRightAlt = false, bool initialWin = false, bool initialF12 = false, bool initialScroll = false)
            : this(initialAlt, initialCtrl)
        {
            inputSwapped = swapped;
            this.swapEnabled = swapEnabled;
            rightAltDown = initialRightAlt;
            nativeWinDown = initialWin;
            rawF12Down = initialF12;
            rawScrollDown = initialScroll;
        }
        public void Confirm() { recoveryAlt = recoveryCtrl = recoveryTab = recoveryRightAlt = recoveryWin = false; }

        public KeyAction Handle(uint vk, uint scan, uint flags, bool up, bool preserve, bool bridgeEnabled = false)
        {
            bool extended = (flags & 1) != 0;
            bool alt = !extended && (scan == 0x38 || vk == 0xA4);
            bool ctrl = !extended && (scan == 0x1D || vk == 0xA2);
            bool rightAlt = extended && (scan == 0x38 || vk == 0xA5);
            bool nativeWin = extended && (scan == 0x5B || vk == 0x5B);
            bool tab = !extended && (scan == 15 || vk == 9);
            bool remoteF12 = !extended && (flags & 0x10) != 0 && (scan == 0x58 || vk == 0x7B);
            bool remoteScroll = !extended && (flags & 0x10) != 0 && (scan == 0x46 || vk == 0x91);
            // Remember even unhandled presses: enabling a feature or pressing
            // Alt later must not adopt an existing ordinary key on its repeat.
            bool firstF12 = remoteF12 && !up && !rawF12Down;
            bool firstScroll = remoteScroll && !up && !rawScrollDown;
            if (remoteF12) rawF12Down = !up;
            if (remoteScroll) rawScrollDown = !up;
            var events = new List<ScanEvent>();
            if (waitingForRelease)
            {
                if (alt) leftAlt = !up;
                if (ctrl) leftCtrl = !up;
                if (rightAlt) rightAltDown = !up;
                if (nativeWin) nativeWinDown = !up;
                waitingForRelease = leftAlt || leftCtrl;
                return new KeyAction(false, events.ToArray());
            }
            // Driver/firmware mapping affects device events, but SendInput does
            // not traverse that input path. Normalize only the calibrated source.
            if (swapEnabled && inputSwapped && (flags & 0x10) == 0)
            {
                bool sourceAlt = alt;
                alt = ctrl;
                ctrl = sourceAlt;
            }
            // Keep prior ownership until the OS confirms the complete batch.
            recoveryAlt = outputAlt; recoveryCtrl = outputCtrl; recoveryTab = outputTab;
            recoveryRightAlt = outputRightAlt; recoveryWin = outputWin;
            if (rightAlt)
            {
                rightAltDown = !up;
                if (outputRightAlt)
                {
                    if (up)
                    {
                        outputRightAlt = false;
                        events.Add(new ScanEvent(0x38, true, true));
                    }
                    return new KeyAction(true, events.ToArray());
                }
                return new KeyAction(false, events.ToArray());
            }
            if (nativeWin)
            {
                nativeWinDown = !up;
                // A real Win-up cannot release Win still owned by ScrollLock.
                return new KeyAction(up && bridgeScrollDown, events.ToArray());
            }
            if (alt || ctrl)
            {
                if (up && ((alt && !leftAlt) || (ctrl && !leftCtrl)))
                    return new KeyAction(false, events.ToArray());
                if (alt) { leftAlt = !up; if (up) switching = false; }
                else leftCtrl = !up;
                UpdateModifiers(events);
                return new KeyAction(true, events.ToArray());
            }
            if (remoteScroll && (bridgeScrollDown || (firstScroll && bridgeEnabled)))
            {
                if (!up)
                {
                    if (!bridgeScrollDown)
                    {
                        bridgeScrollDown = outputWin = true;
                        if (!nativeWinDown) events.Add(new ScanEvent(0x5B, false, true));
                    }
                }
                else
                {
                    bridgeScrollDown = outputWin = false;
                    if (!nativeWinDown) events.Add(new ScanEvent(0x5B, true, true));
                }
                return new KeyAction(true, events.ToArray());
            }
            // The Pad transports Alt+Tab as Alt+F12. Decode transport separately
            // from the swap policy, including Ctrl+Tab when preservation is off.
            if (remoteF12 && (bridgeF12Down || (firstF12 && bridgeEnabled && (leftAlt || rightAltDown))))
            {
                bridgeF12Down = !up;
                if (!up)
                {
                    if (leftAlt && swapEnabled && preserve) { switching = true; UpdateModifiers(events); }
                    // Right Alt's original down passed through. From this point
                    // own its up so it is queued after the decoded Tab batch.
                    if (rightAltDown) outputRightAlt = true;
                    outputTab = true;
                    events.Add(new ScanEvent(15, false));
                }
                else if (outputTab && !directTabDown)
                {
                    outputTab = false;
                    events.Add(new ScanEvent(15, true));
                }
                return new KeyAction(true, events.ToArray());
            }
            if (tab && !up && (directTabDown || bridgeF12Down || (leftAlt && (!swapEnabled || preserve || switching))))
            {
                if (leftAlt && swapEnabled && preserve) { switching = true; UpdateModifiers(events); }
                directTabDown = true;
                outputTab = true;
                events.Add(new ScanEvent(15, false));
                return new KeyAction(true, events.ToArray());
            }
            // Pair our Tab-down with our Tab-up, even if Alt was released first.
            if (tab && up && directTabDown)
            {
                directTabDown = false;
                if (outputTab && !bridgeF12Down)
                {
                    outputTab = false;
                    events.Add(new ScanEvent(15, true));
                }
                return new KeyAction(true, events.ToArray());
            }
            return new KeyAction(false, events.ToArray());
        }

        private void UpdateModifiers(List<ScanEvent> events)
        {
            bool wantCtrl = swapEnabled ? leftAlt && !switching : leftCtrl;
            bool wantAlt = swapEnabled ? leftCtrl || (leftAlt && switching) : leftAlt;
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
            if (outputRightAlt || recoveryRightAlt) events.Add(new ScanEvent(0x38, true, true));
            if ((outputWin || recoveryWin) && !nativeWinDown) events.Add(new ScanEvent(0x5B, true, true));
            leftAlt = leftCtrl = switching = outputAlt = outputCtrl = outputTab = false;
            directTabDown = bridgeF12Down = false;
            rawF12Down = rawScrollDown = bridgeScrollDown = false;
            rightAltDown = outputRightAlt = nativeWinDown = outputWin = false;
            waitingForRelease = false;
            Confirm();
            return new KeyAction(false, events.ToArray());
        }
    }
}
