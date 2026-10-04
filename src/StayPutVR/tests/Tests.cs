// Standalone checks for the files in Osc/, which are the only part of StayPutVR that can
// be exercised without a headset. Both suites compile the mod's real source — there is no
// second copy of the encoder or the sender here — with Stubs.cs standing in for the logger,
// the session log and UnityEngine.Time.
//
//   cd src/StayPutVR/tests && dotnet run
//
// Run it from Windows, not WSL: the Windows SDK cannot read a \\wsl.localhost source path.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using StayPutVR.Osc;
using StayPutVR.Trigger;

class Tests
{
    static int fails = 0;

    static void Say(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    static void Check(string what, byte[] got, byte[] want)
    {
        var ok = got.Length == want.Length;
        if (ok) for (var i = 0; i < got.Length; i++) if (got[i] != want[i]) { ok = false; break; }
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}  len={got.Length}  {BitConverter.ToString(got)}");
        if (!ok) { fails++; Console.WriteLine($"     wanted len={want.Length}  {BitConverter.ToString(want)}"); }
    }

    /// <summary>Builds an expected packet: strings go in as ASCII, ints mean that many nul bytes.</summary>
    static byte[] B(params object[] parts)
    {
        var list = new List<byte>();
        foreach (var p in parts)
        {
            if (p is string s) foreach (var c in s) list.Add((byte)c);
            else if (p is int n) for (var i = 0; i < n; i++) list.Add(0);
        }
        return list.ToArray();
    }

    static UdpClient Listen(int port) => new UdpClient(new IPEndPoint(IPAddress.Loopback, port));

    static byte[] ReceiveOne(UdpClient listener, int millis)
    {
        listener.Client.ReceiveTimeout = millis;
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            var n = listener.Client.ReceiveFrom(buffer, ref from);
            var got = new byte[n];
            Array.Copy(buffer, got, n);
            return got;
        }
        catch (SocketException) { return null; }
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ---- OscPacket: the bytes against the OSC 1.0 layout ---------------------------------
    static void Encoder()
    {
        Console.WriteLine("== OscPacket ==");

        const string Shock = "/avatar/parameters/Shock";   // 24 chars -> a whole extra word of nuls

        Check("bool true, 24-char address", OscPacket.Bool(Shock, true), B(Shock, 4, ",T", 2));
        Check("bool false", OscPacket.Bool(Shock, false), B(Shock, 4, ",F", 2));
        Check("bool, 10-char address", OscPacket.Bool("/SPVR_Bite", true), B("/SPVR_Bite", 2, ",T", 2));
        Check("address already on a word boundary still gets a terminator",
              OscPacket.Bool("/abc", true), B("/abc", 4, ",T", 2));

        // int 1 big-endian = 00 00 00 01
        Check("int 1 is big-endian", OscPacket.Int(Shock, 1),
              B(Shock, 4, ",i", 2, 3, ""));

        // 1.0f = 0x3F800000
        var wantFloat = new List<byte>(B(Shock, 4, ",f", 2));
        wantFloat.AddRange(new byte[] { 0x3F, 0x80, 0x00, 0x00 });
        Check("float 1.0 is big-endian IEEE754", OscPacket.Float(Shock, 1f), wantFloat.ToArray());

        var addresses = new (string addr, bool want)[]
        {
            (Shock, true), ("/a", true), ("/SPVR_Bite_Thigh_Left", true),
            ("", false), (null, false), ("   ", false),
            ("avatar/parameters/Shock", false),
            ("/has space", false), ("/wild*card", false), ("/q?", false),
            ("/br[a]cket", false), ("/cur{ly}", false), ("/ha#sh", false), ("/com,ma", false),
            ("/unié", false),
        };
        foreach (var (addr, want) in addresses)
        {
            var got = OscPacket.IsUsableAddress(addr);
            if (got != want) fails++;
            Console.WriteLine($"{(got == want ? "PASS" : "FAIL")} IsUsableAddress({(addr == null ? "null" : "\"" + addr + "\"")}) = {got}");
        }

        // Every OSC packet must be a whole number of 4-byte words.
        var ragged = 0;
        for (var len = 1; len <= 40; len++)
        {
            var addr = "/" + new string('x', len - 1);
            foreach (var d in new[] { OscPacket.Bool(addr, true), OscPacket.Int(addr, 1), OscPacket.Float(addr, 1f) })
                if (d.Length % 4 != 0) { ragged++; Console.WriteLine($"FAIL address length {len} produced {d.Length} bytes"); }
        }
        fails += ragged;
        if (ragged == 0) Console.WriteLine("PASS every packet, address lengths 1..40, is a multiple of 4 bytes");
    }

    // ---- OscSender: the real socket, over loopback ---------------------------------------
    static void Sender()
    {
        Console.WriteLine("\n== OscSender ==");

        const string Shock = "/avatar/parameters/Shock";

        // ---- 1. a trigger and its release arrive byte-identical -------------------------
        var portA = 19001;
        using (var listener = Listen(portA))
        {
            Say(OscSender.Ensure("127.0.0.1", portA), "Ensure opens a socket to 127.0.0.1:" + portA);
            Say(OscSender.TargetDescription == $"127.0.0.1:{portA}", $"TargetDescription is \"127.0.0.1:{portA}\", got \"{OscSender.TargetDescription}\"");

            var trigger = OscPacket.Bool(Shock, true);
            Say(OscSender.Send(trigger, "trigger"), "the trigger reports sent");
            var got = ReceiveOne(listener, 1500);
            Say(Same(got, trigger), $"the trigger arrives byte-identical ({(got == null ? "nothing arrived" : got.Length + " bytes")})");
            if (got != null) Console.WriteLine("      " + BitConverter.ToString(got));

            var release = OscPacket.Bool(Shock, false);
            Say(OscSender.Send(release, "release"), "the release reports sent");
            Say(Same(ReceiveOne(listener, 1500), release), "the release arrives byte-identical");

            // Every value type, so a config change cannot silently produce garbage.
            foreach (var (name, datagram) in new (string, byte[])[]
                     { ("int", OscPacket.Int(Shock, 1)), ("float", OscPacket.Float(Shock, 1f)) })
            {
                OscSender.Send(datagram, name);
                Say(Same(ReceiveOne(listener, 1500), datagram), $"a {name} trigger arrives byte-identical");
            }
        }

        // ---- 2. changing the port retargets, and the old port stops receiving -----------
        var portB = 19002;
        using (var listenerB = Listen(portB))
        {
            Say(OscSender.Ensure("127.0.0.1", portB), "Ensure retargets to " + portB);
            var trigger = OscPacket.Bool(Shock, true);
            OscSender.Send(trigger, "trigger");
            Say(Same(ReceiveOne(listenerB, 1500), trigger), "the trigger arrives at the new port");
        }

        // ---- 3. sending with nothing listening must not poison the next send ------------
        // This is the reason the socket is left unconnected: a connected UDP socket turns the
        // ICMP port-unreachable into a ConnectionReset on the NEXT send.
        var portC = 19003;
        OscSender.Ensure("127.0.0.1", portC);
        var before = OscSender.Failed;
        for (var i = 0; i < 3; i++) OscSender.Send(OscPacket.Bool(Shock, true), "into the void");
        Say(OscSender.Failed == before, $"three sends to a dead port report no failure (Failed {before} -> {OscSender.Failed})");

        using (var listenerC = Listen(portC))
        {
            var trigger = OscPacket.Bool(Shock, true);
            Say(OscSender.Send(trigger, "trigger"), "a send right after those still reports sent");
            Say(Same(ReceiveOne(listenerC, 1500), trigger), "and it arrives — the dead-port sends did not poison the socket");
        }

        // ---- 4. a bad target is refused, not thrown -------------------------------------
        Say(!OscSender.Ensure("127.0.0.1", 0), "port 0 is refused");
        Say(!OscSender.Ensure("127.0.0.1", 70000), "port 70000 is refused");
        Say(!OscSender.Ensure("no.such.host.invalid", 9001), "an unresolvable host is refused");
        Say(!OscSender.Send(OscPacket.Bool(Shock, true), "no socket"), "a send with no socket reports failure rather than throwing");
        Say(OscSender.LastFailureAt > 0f, "the failure timestamp is set, so the overlay can show trouble");

        Say(OscSender.Ensure("127.0.0.1", 9001), "and it recovers on the next good target");
        OscSender.Close();
    }

    // ---- MdnsPacket: the question, and the app's answer ------------------------------------
    // The answer bytes are what the StayPutVR app sends back: tests/MdnsAnswerDump.cpp calls
    // the app's own vendored mdns library with the records common/OSCQueryServer.cpp answers
    // with (StayPutVR on DESKTOP-TEST.local., port 51234, address 192.168.1.20, id 0x1234).
    // Re-run that program and re-copy if the app's answer ever changes.
    static readonly byte[] AppAnswer =
    {
        0x12, 0x34, 0x84, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x04, 0x5F, 0x6F, 0x73,
        0x63, 0x04, 0x5F, 0x75, 0x64, 0x70, 0x05, 0x6C, 0x6F, 0x63, 0x61, 0x6C, 0x00, 0x00, 0x0C, 0x80,
        0x01, 0xC0, 0x0C, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x0C, 0x09, 0x53, 0x74,
        0x61, 0x79, 0x50, 0x75, 0x74, 0x56, 0x52, 0xC0, 0x0C, 0xC0, 0x2D, 0x00, 0x21, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x78, 0x00, 0x15, 0x00, 0x00, 0x00, 0x00, 0xC8, 0x22, 0x0C, 0x44, 0x45, 0x53, 0x4B,
        0x54, 0x4F, 0x50, 0x2D, 0x54, 0x45, 0x53, 0x54, 0xC0, 0x16, 0xC0, 0x4B, 0x00, 0x01, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x78, 0x00, 0x04, 0xC0, 0xA8, 0x01, 0x14,
    };

    // The question the mod sends for id 0x1234; MdnsAnswerDump.cpp carries the same bytes and
    // pushes them through the app library's listen path.
    static readonly byte[] ModQuery =
    {
        0x12, 0x34, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x04, (byte)'_', (byte)'o', (byte)'s', (byte)'c', 0x04, (byte)'_', (byte)'u', (byte)'d', (byte)'p',
        0x05, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0x00,
        0x00, 0x0C, 0x80, 0x01,
    };

    static byte[] WithId(byte[] packet, ushort id)
    {
        var c = (byte[])packet.Clone();
        c[0] = (byte)(id >> 8);
        c[1] = (byte)id;
        return c;
    }

    /// <summary>The fixture with its SRV port (0xC8 0x22 = 51234, the only such pair in it) replaced.</summary>
    static byte[] WithPort(byte[] packet, ushort port)
    {
        var c = (byte[])packet.Clone();
        var at = -1;
        for (var i = 0; i + 1 < c.Length; i++) if (c[i] == 0xC8 && c[i + 1] == 0x22) { at = i; break; }
        if (at < 0) throw new Exception("port bytes not found in the fixture");
        c[at] = (byte)(port >> 8);
        c[at + 1] = (byte)port;
        return c;
    }

    /// <summary>The fixture advertising a different instance of the same length, so every compression pointer still lands.</summary>
    static byte[] Renamed(byte[] packet, string from, string to)
    {
        if (from.Length != to.Length) throw new ArgumentException("same length only");
        var c = (byte[])packet.Clone();
        var f = System.Text.Encoding.ASCII.GetBytes(from);
        for (var i = 0; i + f.Length <= c.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < f.Length && hit; j++) hit = c[i + j] == f[j];
            if (hit) { for (var j = 0; j < f.Length; j++) c[i + j] = (byte)to[j]; return c; }
        }
        throw new Exception($"'{from}' not found in the fixture");
    }

    static void Mdns()
    {
        Console.WriteLine("\n== MdnsPacket ==");

        Check("PTR question for _osc._udp.local., id 0x1234, unicast-response bit",
              MdnsPacket.Query(0x1234, "_osc._udp.local."), ModQuery);
        Say(Same(MdnsPacket.Query(0x1234, "_osc._udp.local"), ModQuery), "the name without its trailing dot encodes the same");

        Say(MdnsPacket.TryParse(AppAnswer, AppAnswer.Length, out var id, out var records), "the app's answer parses");
        Say(id == 0x1234, $"the id is echoed back (got 0x{id:X4})");
        Say(records.Count == 3, $"three records: one answer and two additionals (got {records.Count}: {string.Join("; ", records)})");

        var ptr = records.Find(r => r.Type == MdnsPacket.TypePtr);
        Say(ptr != null && ptr.Name == "_osc._udp.local." && ptr.Target == "StayPutVR._osc._udp.local.",
            $"the PTR's compressed names are followed (got {ptr})");
        var srv = records.Find(r => r.Type == MdnsPacket.TypeSrv);
        Say(srv != null && srv.Name == "StayPutVR._osc._udp.local." && srv.Target == "DESKTOP-TEST.local." && srv.Port == 51234,
            $"the SRV carries the host and port (got {srv})");
        var a = records.Find(r => r.Type == MdnsPacket.TypeA);
        Say(a != null && a.Name == "DESKTOP-TEST.local." && a.Address.ToString() == "192.168.1.20", $"the A record carries the address (got {a})");

        Say(MdnsPacket.TryFindService(records, "StayPutVR", "_osc._udp.local.", out var port, out var host, out var address)
            && port == 51234 && host == "DESKTOP-TEST.local." && address != null && address.ToString() == "192.168.1.20",
            $"TryFindService finds StayPutVR at {host}:{port} = {address}");
        Say(MdnsPacket.TryFindService(records, "stayputvr", "_OSC._udp.local", out var port2, out _, out _) && port2 == 51234,
            "matching is case-insensitive and does not need the trailing dot");
        Say(!MdnsPacket.TryFindService(records, "VRChat", "_osc._udp.local.", out _, out _, out _), "another instance name is not matched");
        Say(!MdnsPacket.TryFindService(records, "StayPutVR", "_oscjson._tcp.local.", out _, out _, out _), "another service type is not matched");

        var renamed = Renamed(AppAnswer, "StayPutVR", "VRChatXYZ");
        Say(MdnsPacket.TryParse(renamed, renamed.Length, out _, out var r2)
            && !MdnsPacket.TryFindService(r2, "StayPutVR", "_osc._udp.local.", out _, out _, out _)
            && MdnsPacket.TryFindService(r2, "VRChatXYZ", "_osc._udp.local.", out var p3, out _, out _) && p3 == 51234,
            "the same answer from another app is parsed but not taken for StayPutVR");

        Say(!MdnsPacket.TryParse(ModQuery, ModQuery.Length, out _, out _), "a query is not accepted as a response");

        // Anything can arrive on a UDP socket; a cut-off packet must be refused, never thrown on.
        var threw = 0;
        var accepted = 0;
        for (var len = 0; len < AppAnswer.Length; len++)
        {
            try
            {
                if (MdnsPacket.TryParse(AppAnswer, len, out _, out var rr)
                    && MdnsPacket.TryFindService(rr, "StayPutVR", "_osc._udp.local.", out _, out _, out _)) accepted++;
            }
            catch (Exception e) { threw++; Console.WriteLine($"      len {len} threw {e.GetType().Name}"); }
        }
        Say(threw == 0 && accepted == 0, $"every truncation of the answer (0..{AppAnswer.Length - 1} bytes) is refused without throwing ({threw} threw, {accepted} accepted)");
    }

    // ---- Discovery: the thread, against a fake app on loopback ----------------------------
    sealed class FakeApp : IDisposable
    {
        private readonly UdpClient _sock;
        private readonly Thread _thread;
        private volatile bool _running = true;
        public volatile bool Answer = true;
        public volatile byte[] Reply;
        public readonly int Port;
        public int Seen;

        public FakeApp(byte[] reply)
        {
            Reply = reply;
            _sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _sock.Client.ReceiveTimeout = 200;
            Port = ((IPEndPoint)_sock.Client.LocalEndPoint).Port;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        private void Run()
        {
            while (_running)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] q;
                try { q = _sock.Receive(ref from); } catch (SocketException) { continue; }
                Seen++;
                if (!Answer || q.Length < 2) continue;
                // Legacy unicast: straight back to the asker's own port, with its id.
                var reply = WithId(Reply, (ushort)((q[0] << 8) | q[1]));
                _sock.Send(reply, reply.Length, from);
            }
        }

        public void Dispose()
        {
            _running = false;
            _thread.Join(1000);
            _sock.Dispose();
        }
    }

    static bool WaitFor(Func<bool> condition, int millis)
    {
        var until = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < until)
        {
            Discovery.Pump();
            if (condition()) return true;
            Thread.Sleep(50);
        }
        Discovery.Pump();
        return condition();
    }

    static int LogCount(string fragment) => StayPutVR.Logger.Lines.FindAll(l => l.Contains(fragment)).Count;

    static void Finder()
    {
        Console.WriteLine("\n== Discovery ==");
        Discovery.SearchIntervalSeconds = 0.3;
        Discovery.RefreshIntervalSeconds = 0.3;
        Discovery.LostAfterSeconds = 1.0;
        Discovery.ReplyWaitMillis = 300;

        using (var app = new FakeApp(AppAnswer))
        {
            Discovery.QueryTarget = new IPEndPoint(IPAddress.Loopback, app.Port);
            var (h0, p0) = Discovery.Target("127.0.0.1", 9001);
            Say(h0 == "127.0.0.1" && p0 == 9001, "before anything answers, Target is the fallback");
            Say(Discovery.Describe().StartsWith("Port setting"), $"and the panel says so (\"{Discovery.Describe()}\")");

            Discovery.Start();
            Say(WaitFor(() => Discovery.Endpoint != null, 3000), $"the app is found within 3 s ({Discovery.Stats()})");
            var ep = Discovery.Endpoint;
            Say(ep != null && IPAddress.IsLoopback(ep.Address) && ep.Port == 51234, $"an app on this machine is reached as loopback:51234 (got {ep})");
            var (h1, p1) = Discovery.Target("127.0.0.1", 9001);
            Say(h1 == "127.0.0.1" && p1 == 51234, $"Target is now the advertised port ({h1}:{p1})");
            Say(Discovery.Describe() == "via OSC Query", $"the panel says via OSC Query (\"{Discovery.Describe()}\")");
            Say(LogCount("the StayPutVR app is at 127.0.0.1:51234") == 1, "the arrival was logged, once");
            Thread.Sleep(800);
            Say(LogCount("the StayPutVR app is at 127.0.0.1:51234") == 1, "and repeated answers do not log again");
            Say(app.Seen >= 2, $"the question is repeated while the app is known ({app.Seen} seen)");

            // The app goes quiet: after LostAfterSeconds the Port setting takes over.
            app.Answer = false;
            Say(WaitFor(() => Discovery.Endpoint == null, 4000), "after the app stops answering, the endpoint is dropped");
            var (h2, p2) = Discovery.Target("127.0.0.1", 9001);
            Say(h2 == "127.0.0.1" && p2 == 9001, "Target is the fallback again");
            Say(LogCount("no answer from the StayPutVR app") == 1, "the loss was logged, once");
            Say(Discovery.Describe().Contains("stopped answering"), $"the panel says the app stopped answering (\"{Discovery.Describe()}\")");

            // It comes back on another port, as a restarted app does.
            app.Reply = WithPort(AppAnswer, 51235);
            app.Answer = true;
            Say(WaitFor(() => Discovery.Endpoint != null && Discovery.Endpoint.Port == 51235, 3000), $"the app back on another port is picked up ({Discovery.Endpoint})");
            Say(LogCount("the StayPutVR app is at 127.0.0.1:51235") == 1, "the return was logged");

            // And moves without going quiet first.
            app.Reply = WithPort(AppAnswer, 51236);
            Say(WaitFor(() => Discovery.Endpoint != null && Discovery.Endpoint.Port == 51236, 3000), $"a port change is picked up ({Discovery.Endpoint})");
            Say(LogCount("moved to 127.0.0.1:51236") == 1, "the move was logged");

            Discovery.Stop();
            Say(!Discovery.Running && Discovery.Endpoint == null, "Stop clears the endpoint");
        }

        // Another OSC app answering the same question is not taken for StayPutVR.
        using (var other = new FakeApp(Renamed(AppAnswer, "StayPutVR", "VRChatXYZ")))
        {
            Discovery.QueryTarget = new IPEndPoint(IPAddress.Loopback, other.Port);
            Discovery.Start();
            var found = WaitFor(() => Discovery.Endpoint != null, 1500);
            Say(!found && other.Seen > 0, $"an answer for another instance name is ignored ({other.Seen} question(s) seen, {Discovery.Answers} taken)");
            Discovery.Stop();
        }

        // Nothing listening at all: no answer, no error, still the fallback.
        Discovery.QuietQuestions = 3;
        Discovery.QueryTarget = new IPEndPoint(IPAddress.Loopback, 19004);
        Discovery.Start();
        Thread.Sleep(1000);
        Discovery.Pump();
        Say(Discovery.Endpoint == null && Discovery.LastError.Length == 0, $"a dead target is silence, not an error ({Discovery.Stats()})");
        // Silence long enough reads as an app older than 1.5.2 (or OSC Query off), said once.
        Say(WaitFor(() => Discovery.NeverAnswered, 4000), $"after {Discovery.QuietQuestions} unanswered questions NeverAnswered is set ({Discovery.Stats()})");
        Thread.Sleep(800);
        Discovery.Pump();
        Say(LogCount("older than 1.5.2") == 1, "the old-app warning is logged, once");
        Discovery.Stop();
        Say(!Discovery.NeverAnswered, "Stop clears it");

        // An app that answered once and went quiet is not an old app.
        using (var app = new FakeApp(AppAnswer))
        {
            Discovery.QueryTarget = new IPEndPoint(IPAddress.Loopback, app.Port);
            Discovery.Start();
            WaitFor(() => Discovery.Endpoint != null, 3000);
            app.Answer = false;
            Thread.Sleep(2500);
            Discovery.Pump();
            Say(!Discovery.NeverAnswered && LogCount("older than 1.5.2") == 1, $"an app that has answered never triggers the warning ({Discovery.Stats()})");
            Discovery.Stop();
        }
    }

    // ---- Severity: how hard a hit shocks ---------------------------------------------------
    static void Hurt()
    {
        Console.WriteLine("\n== Severity ==");
        const float curve = 0.5f, fallFloor = 0.5f;
        float V(float fraction, float remaining, string type = "Melee", bool downed = false) =>
            Severity.Compute(fraction, remaining, type, downed, curve, fallFloor);
        bool Near(float a, float b) => Math.Abs(a - b) < 0.005f;

        var chip = V(0.10f, 0.90f);
        var lowChip = V(0.10f, 0.10f);
        var half = V(0.50f, 0.50f);
        var fatal = V(0.20f, 0f, downed: true);
        Say(Near(chip, 0.32f), $"10% at full health -> {chip:0.00} (share 0.10 ^ 0.5)");
        Say(Near(lowChip, 0.71f), $"10% with 20% left -> {lowChip:0.00} (share 0.50 ^ 0.5)");
        Say(Near(half, 0.71f), $"50% at full health -> {half:0.00}, the same as 10% at 20%");
        Say(chip < lowChip && lowChip < fatal, "the ordering is chip < low chip < fatal");
        Say(Near(fatal, 1f), $"the killing blow is 1 ({fatal:0.00})");
        Say(Near(V(1f, 0f), 1f), "taking the whole bar is 1 even when not flagged downed");
        Say(Near(Severity.Share(0.10f, 0.90f), 0.10f) && Near(Severity.Share(0.10f, 0.10f), 0.50f), "Share is damage over what you had");

        var fallSmall = V(0.05f, 0.95f, "Fall");
        var fallBig = V(0.80f, 0.20f, "Fall");
        Say(Near(fallSmall, half), $"a small fall reads as the floor, 0.5 share -> {fallSmall:0.00}");
        Say(fallBig > fallSmall, $"a big fall still reads bigger ({fallBig:0.00})");
        Say(Near(V(0.05f, 0.95f, "fall"), fallSmall), "the type match is case-insensitive");

        Say(Near(Severity.Compute(0.10f, 0.90f, "Melee", false, 1f, 0.5f), 0.10f), "curve 1 is linear");
        Say(Near(Severity.Compute(0.10f, 0.90f, "Melee", false, 0f, 0.5f), 0.10f), "a curve of 0 is treated as 1, not as 'everything is max'");
        Say(V(0f, 1f) == Severity.Least, $"a zero-damage hit sends the least, not zero ({Severity.Least})");
        Say(Near(V(0f, 0f), 1f), "damage into an empty bar is 1, not a division by zero");

        var mono = true;
        var last = 0f;
        for (var f = 0.01f; f <= 1f; f += 0.01f)
        {
            var v = V(f, 1f - f);
            if (v < last) mono = false;
            last = v;
            if (v < Severity.Least || v > 1f) mono = false;
        }
        Say(mono, "from a full bar, harder hits never shock less, and every value is within [Least, 1]");
    }

    // ---- HealShield: the time after a heal, per source, and stacking ------------------------------------
    static void Shield()
    {
        Console.WriteLine("\n== Heal shield ==");
        bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;
        var s = new HealShield();
        Say(!s.Active(0f) && !s.Holds(0f, false) && s.Fraction(0f) == 0f, "a new shield holds nothing");

        Say(s.Start(10f, 10f, "minor healing potion"), "a heal starts it");
        Say(s.Holds(10f, false) && Near(s.Left(10f), 10f) && Near(s.Fraction(10f), 1f), "right after the heal: holds, 10 s left, full");
        Say(s.Holds(16f, false) && Near(s.Left(16f), 4f) && Near(s.Fraction(16f), 0.4f), "6 s later: 4 s left, 40%");
        Say(!s.Holds(16f, true), "the killing blow is never held");
        Say(!s.Holds(20f, false) && !s.Active(20f) && s.Left(20f) == 0f, "it ends at exactly 10 s");
        Say(!s.Holds(25f, false) && s.Fraction(25f) == 0f, "and stays ended");
        Say(s.Source == "minor healing potion", "it remembers what healed you");

        // The later end wins; nothing shortens a running shield.
        s.Start(30f, 20f, "major healing potion");            // ends at 50
        Say(!s.Start(35f, 2f, "life steal") && Near(s.Left(35f), 15f) && s.Source == "major healing potion",
            "life steal during a major potion's shield changes nothing");
        Say(!s.Start(36f, 10f, "minor healing potion") && Near(s.Left(36f), 14f), "nor does a minor potion that would end sooner");
        Say(s.Start(45f, 10f, "minor healing potion") && Near(s.Left(45f), 10f) && Near(s.Fraction(45f), 1f) && s.Source == "minor healing potion",
            "a minor potion that ends later moves the end, and the bar is full again");

        // Staff ticks and life steal stack: +2 s each from the running end, never past now + 45 s.
        var st = new HealShield();
        Say(st.Stack(100f, 2f, 45f, "healing staff") && Near(st.Left(100f), 2f) && Near(st.Fraction(100f), 1f), "the first staff tick buys 2 s");
        for (var t = 101f; t <= 110f; t += 1f) st.Stack(t, 2f, 45f, "healing staff");
        // ends at 100 + 11 ticks * 2 s = 122
        Say(Near(st.Left(110f), 12f), "eleven ticks a second apart: 22 s bought, 12 s left at the last one");
        Say(st.Holds(121.9f, false) && !st.Holds(122.1f, false), "and it runs out 12 s after the last tick");
        var lng = new HealShield();
        for (var t = 0f; t <= 60f; t += 1f) lng.Stack(t, 2f, 45f, "healing staff");
        Say(Near(lng.Left(60f), 45f), "a minute of beam: capped at 45 s left");
        Say(!lng.Stack(60f, 2f, 45f, "healing staff") && Near(lng.Left(60f), 45f), "a tick at the cap changes nothing");
        Say(lng.Stack(61f, 2f, 45f, "healing staff") && Near(lng.Left(61f), 45f), "a second on, the next tick tops it back up to 45 s");
        Say(!lng.Holds(106.1f, false), "and the shield ends 45 s after the beam stops");

        var mix = new HealShield();
        mix.Start(200f, 20f, "major healing potion");                    // ends at 220
        Say(mix.Stack(205f, 2f, 45f, "life steal") && Near(mix.Left(205f), 17f), "life steal adds its 2 s on top of a potion's 15 s left");
        Say(!mix.Start(206f, 10f, "minor healing potion") && Near(mix.Left(206f), 16f), "a minor potion that would end sooner changes nothing");
        Say(mix.Start(206f, 20f, "major healing potion") && Near(mix.Left(206f), 20f), "a major potion that ends later moves the end to now + 20 s");
        var big = new HealShield();
        big.Start(300f, 60f, "major healing potion");                     // a potion setting above the cap
        Say(!big.Stack(301f, 2f, 45f, "life steal") && Near(big.Left(301f), 59f), "a steal never shortens a potion shield that runs past the cap");
        var tiny = new HealShield();
        Say(tiny.Stack(400f, 5f, 3f, "life steal") && Near(tiny.Left(400f), 5f), "a cap under one tick counts as one tick");
        Say(!tiny.Stack(400f, 0f, 45f, "life steal") && Near(tiny.Left(400f), 5f), "0 seconds per tick adds nothing");
        Say(!st.Start(115f, 5f, "minor healing potion") && st.Source == "healing staff", "a potion inside a longer staff shield does not cut it short");

        s.Clear();
        Say(!s.Active(55f), "Clear ends it (a scene change, going down)");

        var off = new HealShield();
        Say(!off.Start(40f, 0f, "healing staff") && !off.Holds(40f, false), "0 seconds is off: nothing starts");
        Say(!off.Start(40f, -1f, "healing staff") && !off.Active(40f), "and so is a negative setting");
        s.Start(60f, 10f, "minor healing potion");
        Say(!s.Start(61f, 0f, "life steal") && s.Active(61f) && s.Source == "minor healing potion", "a source set to 0 leaves a running shield alone");

        Say(HealSources.FromPotionType(18) == HealSource.MinorPotion, "Prop.Type 18 (HealthPotionSmall) is a minor potion");
        Say(HealSources.FromPotionType(12) == HealSource.MajorPotion, "Prop.Type 12 (HealthPotion) is a major potion");
        Say(HealSources.FromPotionType(-1) == HealSource.MajorPotion, "anything else counts as major");
    }

    // ---- StaffBeam: the beam RPC as a second witness for staff heals ------------------------
    static void Beam()
    {
        Console.WriteLine("\n== Staff beam ==");
        var b = new StaffBeam();
        Say(!b.Covers(0f), "no beam, nothing covered");

        Say(b.OnBeam(10f, 3, StaffBeam.HealKinetic), "the first beam starts an episode (logged)");
        Say(b.Covers(10f) && b.Covers(10.7f) && !b.Covers(10.8f), "a heal up to 0.75 s after a beam is the staff's");
        b.OnHeal();
        Say(b.Poll(10.9f) == (false, false), "a beam that was followed by a heal is not reported");
        Say(!b.OnBeam(11f, 3, StaffBeam.HealKinetic) && b.Beams == 2, "the next tick is the same episode");
        b.OnHeal();
        Say(b.Poll(13.9f) == (false, false), "2.9 s quiet: still in the episode");
        Say(b.Poll(14.1f) == (false, true) && b.Beams == 2 && b.Heals == 2 && !b.InEpisode, "3 s quiet ends it, counts kept for the log");

        Say(b.OnBeam(20f, 3, StaffBeam.HealKinetic), "a beam after the gap is a new episode");
        Say(b.Poll(20.5f) == (false, false), "no heal yet, but still inside the window");
        Say(b.Poll(20.8f) == (true, false), "no heal within 0.75 s: reported");
        b.OnBeam(21f, 3, StaffBeam.HealKinetic);
        Say(b.Poll(21.9f) == (false, false), "and only once per episode");
        Say(b.Heals == 0 && b.Beams == 2, "the episode counts beams without heals");

        // One shield tick per beam: the heal it causes claims it, and nothing else can.
        var c = new StaffBeam();
        Say(!c.Claim(), "no beam, nothing to claim");
        c.OnBeam(40f, 3, StaffBeam.HealKinetic);
        Say(c.Claim(), "the heal a beam causes claims it");
        Say(c.Covers(40.3f) && !c.Claim(), "a second heal inside the window (the game's own regain) cannot claim it again");
        c.OnBeam(41f, 3, StaffBeam.HealKinetic);
        Say(c.Claim() && !c.Claim(), "the next beam is the next tick, once");
        c.OnBeam(42f, 3, StaffBeam.HealKinetic);
        c.Clear();
        Say(!c.Claim(), "Clear drops an unclaimed beam");

        var push = new StaffBeam();
        push.OnBeam(30f, 3, 1);
        Say(!push.Covers(30.1f), "a Push beam does not make a heal the staff's");
        var unread = new StaffBeam();
        unread.OnBeam(30f, 3, StaffBeam.UnknownKinetic);
        Say(unread.Covers(30.1f), "an unreadable kinetic type still counts");
        unread.Clear();
        Say(!unread.Covers(30.2f) && !unread.InEpisode, "Clear forgets the beam (scene change)");
    }

    static void Main()
    {
        Encoder();
        Sender();
        Mdns();
        Finder();
        Hurt();
        Shield();
        Beam();
        Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILURE(S)");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
