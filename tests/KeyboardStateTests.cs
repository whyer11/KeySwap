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
    static KeyAction Key(uint vk, uint scan, bool up, uint flags = 0, bool preserve = true)
    { var result = state.Handle(vk, scan, flags, up, preserve); Apply(result); state.Confirm(); if (!result.Handled && scan == 15) { if (up) down.Remove(15); else down.Add(15); } return result; }
    static void Alt(bool up) { Key(0xA4, 0x38, up); }
    static void Ctrl(bool up) { Key(0xA2, 0x1D, up); }
    static void Tab(bool up) { Key(9, 15, up); }
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
        #endif
        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
