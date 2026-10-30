using System;
using System.Collections.Generic;
using KeySwap;

internal static class KeyboardStateTests
{
    static int failures;
    static KeyboardState state;
    static HashSet<ushort> down;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Apply(KeyAction action) { foreach (var e in action.Events) { if (e.Up) down.Remove(e.Scan); else down.Add(e.Scan); } }
    static KeyAction Key(uint vk, uint scan, bool up, uint flags = 0, bool preserve = true, bool workBuddy = false)
    {
        #if LEGACY
        var result = state.Handle(vk, scan, flags, up, preserve);
        #else
        var result = state.Handle(vk, scan, flags, up, preserve, workBuddy);
        #endif
        Apply(result); state.Confirm();
        if (!result.Handled && (scan == 15 || vk == 0x5B)) {
            ushort passedScan = vk == 0x5B ? (ushort)0x5B : (ushort)15;
            if (up) down.Remove(passedScan); else down.Add(passedScan);
        }
        return result;
    }
    static void Alt(bool up) { Key(0xA4, 0x38, up); }
    static void Ctrl(bool up) { Key(0xA2, 0x1D, up); }
    static void Tab(bool up) { Key(9, 15, up); }
    #if !LEGACY
    static void RemoteAlt(bool up) { Key(0xA4, 0x38, up, up ? 0x90u : 0x10u); }
    static KeyAction BridgeF12(bool up, bool preserve = true, bool workBuddy = true, uint scan = 0x58)
    { return Key(0x7B, scan, up, up ? 0x90u : 0x10u, preserve, workBuddy); }
    static bool SendsTabDown(KeyAction action)
    { foreach (var e in action.Events) if (e.Scan == 15 && !e.Up) return true; return false; }
    #endif
    static void Test(string name, Action test)
    { state = new KeyboardState(); down = new HashSet<ushort>(); try { test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    public static int Main()
    {
        Test("left Alt becomes Ctrl", delegate { Alt(false); Check(down.SetEquals(new ushort[]{0x1D}), "Ctrl must be held alone"); Alt(true); Check(down.Count == 0, "release"); });
        Test("left Ctrl becomes Alt", delegate { Ctrl(false); Check(down.SetEquals(new ushort[]{0x38}), "Alt must be held alone"); Ctrl(true); Check(down.Count == 0, "release"); });
        Test("Alt Tab holds Alt through repeated Tab", delegate { Alt(false); Tab(false); Tab(true); Check(down.SetEquals(new ushort[]{0x38}), "switcher should stay open"); Tab(false); Tab(true); Alt(true); Check(down.Count == 0, "release"); });
        Test("Alt repeat during switcher never reintroduces Ctrl", delegate { Alt(false); Tab(false); Tab(true); Alt(false); Check(!down.Contains(0x1D), "repeat reintroduced Ctrl"); Alt(true); Check(down.Count == 0, "stuck modifier"); });
        Test("releasing Ctrl cannot release Alt owned by switcher", delegate { Alt(false); Tab(false); Tab(true); Ctrl(false); Ctrl(true); Check(down.Contains(0x38), "shared Alt released early"); Alt(true); Check(down.Count == 0, "release"); });
        Test("releasing Alt cannot release Alt owned by Ctrl", delegate { Alt(false); Tab(false); Tab(true); Ctrl(false); Alt(true); Check(down.Contains(0x38), "physical Ctrl still owns Alt"); Ctrl(true); Check(down.Count == 0, "release"); });
        Test("disable releases injected Ctrl", delegate { Alt(false); Apply(state.ReleaseAll()); Check(down.Count == 0, "Ctrl stuck after disable"); });
        Test("disable releases switcher Alt and Tab", delegate { Alt(false); Tab(false); Apply(state.ReleaseAll()); Check(down.Count == 0, "Alt or Tab stuck after disable"); });
        Test("right Alt and Ctrl pass through", delegate { Check(!Key(0xA5,0x38,false,1).Handled,"right Alt"); Check(!Key(0xA3,0x1D,false,1).Handled,"right Ctrl"); Check(down.Count == 0,"modified right side"); });
        Test("disabled preservation maps Alt Tab to Ctrl Tab", delegate { Alt(false); Check(!Key(9,15,false,0,false).Handled,"Tab must pass"); Check(down.Contains(0x1D) && !down.Contains(0x38),"must remain Ctrl"); Tab(true); Alt(true); });
        Test("ordinary letters pass while swapped Ctrl is down", delegate { Alt(false); Check(!Key(0x43,0x2E,false).Handled,"C swallowed"); Check(down.Contains(0x1D),"lost Ctrl"); Alt(true); });
        Test("bare Tab passes unchanged", delegate { Check(!Key(9,15,false).Handled,"bare Tab swallowed"); });
#if !LEGACY
        Test("calibrated physical Alt already reporting Ctrl stays Ctrl", delegate { state=new KeyboardState(false,false,true); Ctrl(false); Check(down.SetEquals(new ushort[]{0x1D}),"double-swapped physical Alt"); Ctrl(true); Check(down.Count==0,"release"); });
        Test("calibrated physical Ctrl already reporting Alt stays Alt", delegate { state=new KeyboardState(false,false,true); Alt(false); Check(down.SetEquals(new ushort[]{0x38}),"double-swapped physical Ctrl"); Alt(true); Check(down.Count==0,"release"); });
        Test("calibrated physical Alt Tab enters preservation path", delegate { state=new KeyboardState(false,false,true); Ctrl(false); Check(Key(9,15,false).Handled,"native Ctrl-reporting Alt skipped preservation"); Tab(true); Check(down.SetEquals(new ushort[]{0x38}),"switcher Alt not held"); Ctrl(true); Check(down.Count==0,"release"); });
        Test("injected input bypasses hardware-only calibration", delegate { state=new KeyboardState(false,false,true); Key(0xA4,0x38,false,0x10); Check(down.SetEquals(new ushort[]{0x1D}),"synthetic input incorrectly calibrated"); Key(0xA4,0x38,true,0x90); Check(down.Count==0,"release"); });
#endif
        Test("unpaired modifier release passes through", delegate { Check(!Key(0xA2,0x1D,true).Handled,"swallowed native Ctrl release after enabling"); Check(!Key(0xA4,0x38,true).Handled,"swallowed native Alt release after enabling"); });
        Test("failed Ctrl release remains available for cleanup", delegate { Alt(false); state.Handle(0xA4,0x38,0,true,true); Apply(state.ReleaseAll()); Check(down.Count==0,"failed key-up lost from recovery"); });
        #if !LEGACY
        Test("initial native Ctrl stays native until released", delegate { state = new KeyboardState(false,true); Check(!Key(0xA2,0x1D,false).Handled,"remapped pre-existing Ctrl repeat"); Check(!Key(0xA2,0x1D,true).Handled,"swallowed pre-existing Ctrl release"); Check(down.Count==0,"injected during startup quarantine"); Alt(false); Check(down.SetEquals(new ushort[]{0x1D}),"mapping did not resume"); Alt(true); Check(down.Count==0,"release"); });
        Test("initial native Alt leaves Alt Tab native", delegate { state = new KeyboardState(true,false); Check(!Key(9,15,false).Handled,"remapped pre-existing Alt Tab"); Tab(true); Check(!Key(0xA4,0x38,true).Handled,"swallowed pre-existing Alt release"); Ctrl(false); Check(down.SetEquals(new ushort[]{0x38}),"mapping did not resume"); Ctrl(true); Check(down.Count==0,"release"); });
        Test("startup quarantine waits for both modifiers", delegate { state = new KeyboardState(true,true); Check(!Key(0xA2,0x1D,true).Handled,"swallowed Ctrl release"); Check(!Key(0xA4,0x38,false).Handled,"remapped remaining native Alt"); Check(!Key(0xA4,0x38,true).Handled,"swallowed Alt release"); Check(down.Count==0,"injected during startup quarantine"); Ctrl(false); Check(down.SetEquals(new ushort[]{0x38}),"mapping did not resume"); Ctrl(true); });
        Test("startup quarantine passes new modifiers until original Ctrl release", delegate { state = new KeyboardState(false,true); Check(!Key(0xA4,0x38,false).Handled,"remapped newly pressed Alt during quarantine"); Check(!Key(0xA4,0x38,true).Handled,"swallowed newly pressed Alt release"); Check(!Key(0xA2,0x1D,true).Handled,"swallowed original Ctrl release"); Check(down.Count==0,"injected during startup quarantine"); Alt(false); Check(down.SetEquals(new ushort[]{0x1D}),"mapping did not resume after original Ctrl release"); Alt(true); Check(down.Count==0,"release"); });
        Test("failed Alt Tab batch cleans every inserted prefix", delegate { for(int prefix=0;prefix<=3;prefix++) { state=new KeyboardState(); down=new HashSet<ushort>(); Alt(false); var transition=state.Handle(9,15,0,false,true); Check(transition.Events.Length==3,"expected Ctrl-up, Alt-down, Tab-down transition"); for(int i=0;i<prefix;i++) Apply(new KeyAction(false,new ScanEvent[]{transition.Events[i]})); Apply(state.ReleaseAll()); Check(down.Count==0,"owned key stuck after inserted prefix length "+prefix); } });
        Test("WorkBuddy injected Alt F12 becomes native Alt Tab", delegate {
            RemoteAlt(false);
            var press = BridgeF12(false);
            Check(press.Handled, "bridge F12-down escaped to Ctrl+F12");
            Check(down.SetEquals(new ushort[]{0x38,15}), "must hold only Alt and Tab");
            Check(BridgeF12(true).Handled, "bridge F12-up escaped");
            Check(down.SetEquals(new ushort[]{0x38}), "switcher Alt must survive Tab release");
            RemoteAlt(true); Check(down.Count==0, "stuck bridge key");
        });
        Test("WorkBuddy F12 repeat emits repeated Tab without Ctrl", delegate {
            RemoteAlt(false); BridgeF12(false);
            var repeat = BridgeF12(false);
            Check(repeat.Handled && SendsTabDown(repeat), "repeat did not emit another Tab-down");
            Check(down.SetEquals(new ushort[]{0x38,15}), "repeat reintroduced Ctrl or F12");
            BridgeF12(true); RemoteAlt(true); Check(down.Count==0, "repeat left key held");
        });
        Test("separate WorkBuddy F12 taps keep switcher open", delegate {
            RemoteAlt(false);
            for(int i=0;i<3;i++) {
                Check(SendsTabDown(BridgeF12(false)), "tap did not cycle switcher");
                Check(BridgeF12(true).Handled, "tap release escaped");
                Check(down.SetEquals(new ushort[]{0x38}), "Alt should remain held between taps");
            }
            RemoteAlt(true); Check(down.Count==0, "tap sequence left key held");
        });
        Test("WorkBuddy F12-up pairs after Alt-up and configuration change", delegate {
            RemoteAlt(false); BridgeF12(false); RemoteAlt(true);
            Check(down.SetEquals(new ushort[]{15}), "Alt release must leave the held Tab paired");
            Check(BridgeF12(true,false,false).Handled, "latched F12-up lost after Alt/preserve/host change");
            Check(down.Count==0, "Tab stuck after quick Alt release");
        });
        Test("WorkBuddy F12-up pairs while Alt held after host disappears", delegate {
            RemoteAlt(false); BridgeF12(false);
            Check(BridgeF12(true,false,false).Handled, "pending F12-up requires host to remain enabled");
            Check(down.SetEquals(new ushort[]{0x38}), "Tab stuck or Alt released early");
            RemoteAlt(true); Check(down.Count==0, "release");
        });
        Test("WorkBuddy sentinel accepts VK F12 without a scan code", delegate {
            RemoteAlt(false);
            Check(BridgeF12(false,true,true,0).Handled, "virtual-key F12 with scan zero not recognized");
            Check(down.SetEquals(new ushort[]{0x38,15}), "scan-zero sentinel did not switch");
            Check(BridgeF12(true,true,true,0).Handled, "scan-zero F12-up not paired");
            RemoteAlt(true); Check(down.Count==0, "release");
        });
        Test("physical F12 is untouched even with WorkBuddy present", delegate {
            Alt(false);
            Check(!Key(0x7B,0x58,false,0,true,true).Handled, "local F12-down became Tab");
            Check(!Key(0x7B,0x58,true,0x80,true,true).Handled, "local F12-up swallowed");
            Check(down.SetEquals(new ushort[]{0x1D}), "local F12 changed swapped Ctrl"); Alt(true);
        });
        Test("bare injected F12 is untouched", delegate {
            Check(!BridgeF12(false).Handled, "bare remote F12-down became Tab");
            Check(!BridgeF12(true).Handled, "bare remote F12-up swallowed");
            Check(down.Count==0, "bare F12 injected modifiers");
        });
        Test("injected Alt F12 is untouched without WorkBuddy", delegate {
            RemoteAlt(false);
            Check(!BridgeF12(false,true,false).Handled, "F12 mapped without compatible host");
            Check(!BridgeF12(true,true,false).Handled, "F12-up swallowed without compatible host");
            Check(down.SetEquals(new ushort[]{0x1D}), "disabled bridge changed modifiers"); RemoteAlt(true);
        });
        Test("bridge decodes Alt F12 to Ctrl Tab when preservation disabled", delegate {
            RemoteAlt(false);
            Check(BridgeF12(false,false,true).Handled, "bridge transport was not decoded with preservation off");
            Check(down.SetEquals(new ushort[]{0x1D,15}), "preservation-off must produce Ctrl Tab");
            Check(BridgeF12(true,false,true).Handled, "bridge F12-up was not paired");
            Check(down.SetEquals(new ushort[]{0x1D}), "preservation-off changed modifiers"); RemoteAlt(true);
        });
        Test("WorkBuddy sentinel requires source left Alt rather than mapped Alt", delegate {
            Key(0xA2,0x1D,false,0x10);
            Check(!BridgeF12(false).Handled, "source Ctrl plus F12 mistaken for bridge Alt Tab");
            Check(!BridgeF12(true).Handled, "ordinary source Ctrl F12-up swallowed");
            Check(down.SetEquals(new ushort[]{0x38}), "lost Alt owned by source Ctrl"); Key(0xA2,0x1D,true,0x90);
        });
        Test("disable releases keys owned by held WorkBuddy F12", delegate {
            RemoteAlt(false); Check(BridgeF12(false).Handled, "bridge was not engaged");
            Apply(state.ReleaseAll()); Check(down.Count==0, "disable left bridge Alt or Tab held");
            Check(!BridgeF12(true,false,false).Handled, "disable did not clear alias ownership");
        });
        Test("failed WorkBuddy batch cleans every inserted prefix", delegate {
            for(int prefix=0;prefix<=3;prefix++) {
                state=new KeyboardState(); down=new HashSet<ushort>(); RemoteAlt(false);
                var transition=state.Handle(0x7B,0x58,0x10,false,true,true);
                Check(transition.Handled && transition.Events.Length==3, "expected Ctrl-up, Alt-down, Tab-down bridge transition");
                for(int i=0;i<prefix;i++) Apply(new KeyAction(false,new ScanEvent[]{transition.Events[i]}));
                Apply(state.ReleaseAll());
                Check(down.Count==0, "bridge key stuck after inserted prefix length "+prefix);
                Check(!BridgeF12(true,false,false).Handled, "failure cleanup retained F12 alias ownership");
            }
        });
        Test("failed WorkBuddy F12-up retains Tab for recovery", delegate {
            RemoteAlt(false); Check(BridgeF12(false).Handled, "bridge was not engaged");
            state.Handle(0x7B,0x58,0x90,true,true,true);
            Apply(state.ReleaseAll()); Check(down.Count==0, "failed bridge key-up lost Tab recovery");
        });
        Test("direct Tab release cannot release Tab owned by WorkBuddy F12", delegate {
            RemoteAlt(false); Key(9,15,false,0x10,true,true); BridgeF12(false);
            Check(Key(9,15,true,0x90,true,true).Handled, "owned direct Tab-up escaped");
            Check(down.SetEquals(new ushort[]{0x38,15}), "direct Tab-up released bridge-owned Tab");
            Check(BridgeF12(true).Handled, "bridge release escaped");
            Check(down.SetEquals(new ushort[]{0x38}), "last Tab owner did not release Tab");
            RemoteAlt(true); Check(down.Count==0, "release");
        });
        Test("WorkBuddy F12 release cannot release Tab owned by direct Tab", delegate {
            RemoteAlt(false); BridgeF12(false); Key(9,15,false,0x10,true,true);
            Check(BridgeF12(true).Handled, "owned bridge release escaped");
            Check(down.SetEquals(new ushort[]{0x38,15}), "bridge release released direct-owned Tab");
            Check(Key(9,15,true,0x90,true,true).Handled, "direct release escaped");
            Check(down.SetEquals(new ushort[]{0x38}), "last Tab owner did not release Tab");
            RemoteAlt(true); Check(down.Count==0, "release");
        });
        Test("swap off keeps left modifiers native", delegate {
            state=new KeyboardState(false,false,false,false);
            Alt(false); Check(down.SetEquals(new ushort[]{0x38}), "swap-off Alt became Ctrl"); Alt(true);
            Ctrl(false); Check(down.SetEquals(new ushort[]{0x1D}), "swap-off Ctrl became Alt"); Ctrl(true);
            Check(down.Count==0, "native modifiers left held");
        });
        Test("swap off does not apply boot input calibration", delegate {
            state=new KeyboardState(false,false,true,false);
            Alt(false); Check(down.SetEquals(new ushort[]{0x38}), "swap-off calibrated native Alt"); Alt(true);
        });
        Test("swap off bridge Alt F12 works with preservation off", delegate {
            state=new KeyboardState(false,false,false,false);
            RemoteAlt(false); Check(BridgeF12(false,false,true).Handled, "standalone bridge did not decode");
            Check(down.SetEquals(new ushort[]{0x38,15}), "standalone bridge must hold Alt Tab");
            RemoteAlt(true); Check(down.SetEquals(new ushort[]{15}), "fast Alt release lost pending Tab");
            BridgeF12(true,false,true); Check(down.Count==0, "standalone bridge left keys held");
        });
        Test("unowned F12 press cannot be adopted when bridge enabled on repeat", delegate {
            RemoteAlt(false); Check(!BridgeF12(false,true,false).Handled, "disabled bridge consumed first down");
            Check(!BridgeF12(false,true,true).Handled, "bridge adopted already-held F12 repeat");
            Check(!BridgeF12(true,true,true).Handled, "bridge swallowed unowned F12-up");
            Check(BridgeF12(false,true,true).Handled, "new F12 press was not eligible"); BridgeF12(true); RemoteAlt(true);
        });
        Test("bare F12 press cannot be adopted when Alt pressed afterward", delegate {
            Check(!BridgeF12(false).Handled, "bare first down consumed"); RemoteAlt(false);
            Check(!BridgeF12(false).Handled, "bridge adopted F12 held before Alt");
            Check(!BridgeF12(true).Handled, "unowned F12 release swallowed"); RemoteAlt(true);
        });
        Test("right Alt bridge claims ordered release without swapping right Alt", delegate {
            Check(!Key(0xA5,0x38,false,0x11,true,true).Handled, "ordinary right Alt should pass");
            Check(BridgeF12(false).Handled, "right Alt F12 was not decoded");
            Check(down.SetEquals(new ushort[]{15}), "right Alt bridge changed left modifiers");
            var release=Key(0xA5,0x38,true,0x91,true,true);
            Check(release.Handled && release.Events.Length==1 && release.Events[0].Scan==0x38 && release.Events[0].Extended && release.Events[0].Up, "right Alt-up must be emitted after queued Tab-down");
            Check(BridgeF12(true).Handled, "right Alt bridge F12-up escaped"); Check(down.Count==0, "right Alt bridge left Tab held");
        });
        Test("ordinary right Alt remains passthrough with bridge enabled", delegate {
            Check(!Key(0xA5,0x38,false,0x11,true,true).Handled, "ordinary right Alt-down changed");
            Check(!Key(0xA5,0x38,true,0x91,true,true).Handled, "ordinary right Alt-up changed");
            Check(state.ReleaseAll().Events.Length==0, "ordinary right Alt incorrectly owned");
        });
        Test("remote ScrollLock becomes held extended Windows key", delegate {
            var press=Key(0x91,0x46,false,0x10,true,true);
            Check(press.Handled && press.Events.Length==1 && press.Events[0].Scan==0x5B && press.Events[0].Extended && !press.Events[0].Up, "Win-down missing extended scan E0 5B");
            Check(!Key(0x45,0x12,false,0x10,true,true).Handled, "Win+E letter swallowed");
            Check(down.SetEquals(new ushort[]{0x5B}), "Win not held across shortcut letter");
            var repeat=Key(0x91,0x46,false,0x10,true,true); Check(repeat.Handled && repeat.Events.Length==0, "Scroll repeat emitted extra Win-down");
            var release=Key(0x91,0x46,true,0x90,false,false);
            Check(release.Handled && release.Events.Length==1 && release.Events[0].Extended && release.Events[0].Up, "owned Win-up not paired after configuration change");
            Check(down.Count==0, "Win stuck");
        });
        Test("local ScrollLock and disabled bridge ScrollLock pass", delegate {
            Check(!Key(0x91,0x46,false,0,true,true).Handled, "local ScrollLock remapped");
            Check(!Key(0x91,0x46,true,0x80,true,true).Handled, "local ScrollLock-up swallowed");
            Check(!Key(0x91,0x46,false,0x10,true,false).Handled, "disabled remote ScrollLock remapped");
            Check(!Key(0x91,0x46,true,0x90,true,false).Handled, "disabled remote ScrollLock-up swallowed");
            Check(down.Count==0, "unowned ScrollLock emitted keys");
        });
        Test("unowned ScrollLock cannot be adopted on repeat", delegate {
            Check(!Key(0x91,0x46,false,0x10,true,false).Handled, "disabled first down consumed");
            Check(!Key(0x91,0x46,false,0x10,true,true).Handled, "already-held ScrollLock adopted");
            Check(!Key(0x91,0x46,true,0x90,true,true).Handled, "unowned ScrollLock-up swallowed");
            Check(Key(0x91,0x46,false,0x10,true,true).Handled, "fresh ScrollLock not decoded");
            Key(0x91,0x46,true,0x90,true,true); Check(down.Count==0, "release");
        });
        Test("bridge Windows key works with swap disabled", delegate {
            state=new KeyboardState(false,false,false,false);
            Check(Key(0x91,0,false,0x10,false,true).Handled, "standalone scan-zero ScrollLock not decoded");
            Check(down.SetEquals(new ushort[]{0x5B}), "standalone Windows key absent");
            Key(0x91,0,true,0x90,false,true); Check(down.Count==0, "release");
        });
        Test("shutdown releases owned Windows key only", delegate {
            Check(state.ReleaseAll().Events.Length==0, "unowned Windows key released");
            Check(Key(0x91,0x46,false,0x10,true,true).Handled, "bridge did not own Win");
            var cleanup=state.ReleaseAll();
            Check(cleanup.Events.Length==1 && cleanup.Events[0].Scan==0x5B && cleanup.Events[0].Extended && cleanup.Events[0].Up, "owned Win cleanup not extended key-up");
            Apply(cleanup); Check(down.Count==0, "Win stuck after shutdown");
        });
        Test("failed Windows key release retains recovery ownership", delegate {
            Key(0x91,0x46,false,0x10,true,true);
            Check(down.Contains(0x5B), "bridge did not own Win");
            state.Handle(0x91,0x46,0x90,true,true,true);
            Apply(state.ReleaseAll()); Check(down.Count==0, "failed Win-up lost recovery");
        });
        Test("failed Windows key press cleans inserted and empty prefixes", delegate {
            for(int prefix=0;prefix<=1;prefix++) {
                state=new KeyboardState(); down=new HashSet<ushort>();
                var press=state.Handle(0x91,0x46,0x10,false,true,true);
                Check(press.Handled && press.Events.Length==1, "Win press not handled");
                if(prefix==1) Apply(press);
                Apply(state.ReleaseAll()); Check(down.Count==0, "Win stuck after partial send");
            }
        });
        Test("swap off direct Alt Tab retains ordered Tab ownership", delegate {
            state=new KeyboardState(false,false,false,false); RemoteAlt(false);
            Check(Key(9,15,false,0x10,false,true).Handled, "native-mode Tab-down not owned");
            RemoteAlt(true); Check(down.SetEquals(new ushort[]{15}), "Alt-up must follow owned Tab-down");
            Check(Key(9,15,true,0x90,false,true).Handled, "native-mode Tab-up not paired");
            Check(down.Count==0, "native-mode fast release left Tab held");
        });
        Test("swap off calibration cannot turn source Ctrl into bridge Alt", delegate {
            state=new KeyboardState(false,false,true,false); Ctrl(false);
            Check(!BridgeF12(false).Handled, "swap-off source Ctrl was incorrectly normalized to Alt");
            BridgeF12(true); Ctrl(true); Check(down.Count==0, "release");
        });
        Test("shutdown leaves ordinary native right Alt untouched", delegate {
            Key(0xA5,0x38,false,0x11,true,true);
            Check(state.ReleaseAll().Events.Length==0, "unowned right Alt released at shutdown");
        });
        Test("shutdown releases right Alt claimed by bridge", delegate {
            Key(0xA5,0x38,false,0x11,true,true); BridgeF12(false);
            var cleanup=state.ReleaseAll(); bool rightUp=false;
            foreach(var e in cleanup.Events) if(e.Scan==0x38 && e.Up && e.Extended) rightUp=true;
            Check(rightUp, "claimed right Alt not released"); Apply(cleanup); Check(down.Count==0, "bridge Tab stuck");
        });
        Test("failed claimed right Alt release retains recovery", delegate {
            Key(0xA5,0x38,false,0x11,true,true); BridgeF12(false);
            state.Handle(0xA5,0x38,0x91,true,true,true);
            var cleanup=state.ReleaseAll(); bool rightUp=false;
            foreach(var e in cleanup.Events) if(e.Scan==0x38 && e.Up && e.Extended) rightUp=true;
            Check(rightUp, "failed extended right Alt-up lost recovery"); Apply(cleanup); Check(down.Count==0, "Tab stuck");
        });
        Test("bridge Windows release leaves native Windows owner held", delegate {
            Check(!Key(0x5B,0x5B,false,1,true,true).Handled, "native Win-down swallowed");
            var press=Key(0x91,0x46,false,0x10,true,true);
            Check(press.Handled && press.Events.Length==0, "bridge redundantly pressed already-held native Win");
            var release=Key(0x91,0x46,true,0x90,true,true);
            Check(release.Handled && release.Events.Length==0, "bridge released Win still held natively");
            Check(down.Contains(0x5B), "native Win lost");
            Check(!Key(0x5B,0x5B,true,0x81,true,true).Handled, "native Win-up swallowed after bridge release");
            Check(down.Count==0, "native Win stuck");
        });
        Test("native Windows release leaves bridge Windows owner held", delegate {
            Key(0x91,0x46,false,0x10,true,true);
            Key(0x5B,0x5B,false,1,true,true);
            Check(Key(0x5B,0x5B,true,0x81,true,true).Handled, "native Win-up released bridge-owned Win");
            Check(down.Contains(0x5B), "bridge Win released prematurely");
            Key(0x91,0x46,true,0x90,true,true); Check(down.Count==0, "last Win owner failed to release");
        });
        Test("shutdown preserves independently held native Windows key", delegate {
            Key(0x91,0x46,false,0x10,true,true); Key(0x5B,0x5B,false,1,true,true);
            var cleanup=state.ReleaseAll();
            foreach(var e in cleanup.Events) Check(e.Scan!=0x5B, "shutdown released independently held native Win");
            Apply(cleanup); Check(down.Contains(0x5B), "native Win should remain held");
            Key(0x5B,0x5B,true,0x81,true,true); Check(down.Count==0, "native Win release");
        });
        Test("startup-quarantined ScrollLock cannot be adopted on repeat", delegate {
            state=new KeyboardState(false,true);
            Check(!Key(0x91,0x46,false,0x10,true,true).Handled, "quarantine consumed ScrollLock-down");
            Ctrl(true);
            Check(!Key(0x91,0x46,false,0x10,true,true).Handled, "quarantined held ScrollLock adopted");
            Check(!Key(0x91,0x46,true,0x90,true,true).Handled, "quarantined release swallowed");
            Check(Key(0x91,0x46,false,0x10,true,true).Handled, "fresh ScrollLock not decoded after quarantine");
            Key(0x91,0x46,true,0x90,true,true); Check(down.Count==0, "release");
        });
        Test("startup-held right Alt can enter bridge with ordered release", delegate {
            state=new KeyboardState(false,false,false,true,true);
            Check(BridgeF12(false).Handled, "startup-held right Alt not recognized");
            Check(down.SetEquals(new ushort[]{15}), "right Alt bridge changed left modifiers");
            var release=Key(0xA5,0x38,true,0x91,true,true);
            Check(release.Handled && release.Events.Length==1 && release.Events[0].Extended && release.Events[0].Scan==0x38 && release.Events[0].Up, "seeded right Alt-up lost ordered ownership");
            BridgeF12(true); Check(down.Count==0, "seeded right Alt bridge left Tab held");
        });
        Test("startup-held right Alt release stays native without a bridge chord", delegate {
            state=new KeyboardState(false,false,false,true,true);
            Check(!Key(0xA5,0x38,true,0x91,true,true).Handled, "unclaimed seeded right Alt-up swallowed");
            Check(!BridgeF12(false).Handled, "released seeded right Alt remained active");
            Check(state.ReleaseAll().Events.Length==0, "unclaimed right Alt owned at cleanup");
        });
        Test("startup-held native Win survives bridge press and release", delegate {
            state=new KeyboardState(false,false,false,true,false,true); down.Add(0x5B);
            var press=Key(0x91,0x46,false,0x10,true,true);
            Check(press.Handled && press.Events.Length==0, "seeded native Win was redundantly pressed");
            var release=Key(0x91,0x46,true,0x90,true,true);
            Check(release.Handled && release.Events.Length==0, "bridge released seeded native Win");
            Check(down.Contains(0x5B), "seeded native Win lost");
            Check(!Key(0x5B,0x5B,true,0x81,true,true).Handled, "seeded native Win-up swallowed");
            Check(down.Count==0, "seeded native Win stuck");
        });
        Test("startup-held F12 cannot be adopted on its first observed repeat", delegate {
            state=new KeyboardState(false,false,false,true,false,false,true); RemoteAlt(false);
            Check(!BridgeF12(false).Handled, "seeded held F12 adopted on repeat");
            Check(!BridgeF12(true).Handled, "seeded held F12-up swallowed");
            Check(BridgeF12(false).Handled, "fresh F12 press not decoded after seeded release");
            BridgeF12(true); RemoteAlt(true); Check(down.Count==0, "release");
        });
        Test("startup-held ScrollLock cannot be adopted on first observed repeat", delegate {
            state=new KeyboardState(false,false,false,true,false,false,false,true);
            Check(!Key(0x91,0x46,false,0x10,true,true).Handled, "seeded held ScrollLock adopted on repeat");
            Check(!Key(0x91,0x46,true,0x90,true,true).Handled, "seeded held ScrollLock-up swallowed");
            Check(Key(0x91,0x46,false,0x10,true,true).Handled, "fresh ScrollLock not decoded after seeded release");
            Key(0x91,0x46,true,0x90,true,true); Check(down.Count==0, "release");
        });
        #endif
        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
