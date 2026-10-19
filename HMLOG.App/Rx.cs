using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HMLOG.App
{
    class RxLine
    {
        public DateTime T;
        public string L;
        public RxLine(DateTime t, string l) { T = t; L = l; }
    }

    // Serial input: the port, the received lines (text or binary records put together by BinAsm) in a queue,
    // and the file the raw lines are saved to. Same line handling as MainForm.OnSerialData of the Windows version.
    class Rx
    {
        static readonly Encoding Latin1 = Encoding.Latin1;
        public readonly ConcurrentQueue<RxLine> Q = new ConcurrentQueue<RxLine>();
        SerialPort port;
        readonly StringBuilder part = new StringBuilder();
        readonly List<string> binOut = new List<string>();
        BinAsm bin = new BinAsm();
        bool isBin, first;
        public long Bytes;
        public int UartErr, Partial;
        public string Name = "";
        public int Baud;
        public DateTime LastRx = DateTime.MinValue, OpenedAt = DateTime.MinValue;
        public string Error = "";

        public bool IsOpen { get { return port != null && port.IsOpen; } }

        // port names per OS: COMx (Windows), /dev/cu.* (macOS), /dev/ttyUSB* ttyACM* (Linux)
        public static List<string> Ports()
        {
            var names = SerialPort.GetPortNames().Distinct().ToList();
            if (OperatingSystem.IsMacOS()) names = names.Where(n => n.Contains("/cu.") && !n.Contains("Bluetooth")).ToList();
            names.Sort((a, b) =>
            {
                int na = Num(a), nb = Num(b);
                return na != nb ? na.CompareTo(nb) : string.CompareOrdinal(a, b);
            });
            return names;
        }

        static int Num(string n)
        {
            var m = Regex.Match(n, @"(\d+)$");
            return m.Success && n.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? int.Parse(m.Groups[1].Value) : 100000;
        }

        // baud sync (FPGA G_UART_AUTO): a break (line low) and a run of 0x55 bytes at the rate wanted; the board
        // switches its receiver and its log to it (921600 / 2 M / 3 M). Boards before baud detection ignore it.
        public static void SendSync(SerialPort p, int baud)
        {
            try
            {
                try { p.BreakState = true; Thread.Sleep(4); p.BreakState = false; }
                catch (Exception)
                {
                    // an adapter without break: a 0x00 byte at 9600 baud is a low line of about 1 ms
                    p.BaudRate = 9600;
                    p.Write(new byte[] { 0 }, 0, 1);
                    Thread.Sleep(15);
                    p.BaudRate = baud;
                }
                Thread.Sleep(1);
                p.Write(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 }, 0, 8);
                Thread.Sleep(5);
            }
            catch (Exception) { }
        }

        // the rate the board logs at (tries the last good one, then 3 M, 2 M, 921600); 0 = no rate gave a log. Blocks up to ~4 s.
        public static int Probe(string name, int lastGood)
        {
            int[] order = { lastGood, 3000000, 2000000, 921600 };
            var tried = new List<int>();
            foreach (int baud in order)
            {
                if (tried.Contains(baud)) continue;
                tried.Add(baud);
                try
                {
                    using (var p = new SerialPort(name, baud, Parity.None, 8, StopBits.One))
                    {
                        p.DtrEnable = true;
                        p.RtsEnable = true;
                        p.Open();
                        SendSync(p, baud);
                        var asm = new BinAsm();
                        var lines = new List<string>();
                        DateTime end = DateTime.Now.AddMilliseconds(1300);
                        while (DateTime.Now < end)
                        {
                            Thread.Sleep(40);
                            int n = p.BytesToRead;
                            while (n-- > 0) asm.Feed(p.ReadByte(), lines);
                            if (lines.Count >= 2 && asm.SyncErr == 0) return baud;
                        }
                    }
                }
                catch (Exception) { }
            }
            return 0;
        }

        // null when open, else the error text; sync: send the baud sync right after opening (a board still at the rate of
        // an earlier connection follows this one) and drop what came before it
        public string Open(string name, int baud, bool sync = false)
        {
            Close();
            try
            {
                var p = new SerialPort(name, baud, Parity.None, 8, StopBits.One);
                p.ReadBufferSize = 1 << 20;
                p.Encoding = Latin1;                       // every byte kept as one character
                p.DtrEnable = true;                        // many USB CDC adapters only send with DTR on
                p.RtsEnable = true;
                p.ErrorReceived += (s, e) => { Interlocked.Increment(ref UartErr); Q.Enqueue(new RxLine(DateTime.Now, "# uart error " + e.EventType)); };
                p.DataReceived += OnData;
                lock (part) { part.Clear(); isBin = false; first = true; bin = new BinAsm(); }
                p.Open();
                if (sync)
                {
                    SendSync(p, baud);
                    Thread.Sleep(80);                                  // the board switches its receiver and its log
                    try { p.DiscardInBuffer(); } catch (Exception) { }
                    lock (part) { part.Clear(); isBin = false; first = true; bin = new BinAsm(); }
                }
                port = p;
                Name = name; Baud = baud; Error = ""; OpenedAt = DateTime.Now;
                return null;
            }
            catch (Exception ex) { Error = ex.Message; return ex.Message; }
        }

        public void Close()
        {
            var p = port;
            port = null;
            if (p == null) return;
            try { p.DataReceived -= OnData; } catch (Exception) { }
            // closing a port the adapter was unplugged from can block: done on another thread
            var t = new Thread(() => { try { p.Close(); p.Dispose(); } catch (Exception) { } }) { IsBackground = true };
            t.Start();
            t.Join(1500);
        }

        // development aid (capture scripts): the bytes go here instead of the port
        public Func<byte[], bool> TestSink;

        // true when the port (or the test sink) can take bytes
        public bool CanWrite { get { return TestSink != null || IsOpen; } }

        // bytes to the board (control frames); null when sent, else the error text
        public string Write(byte[] data)
        {
            if (TestSink != null) return TestSink(data) ? null : "test sink refused";
            var p = port;
            if (p == null || !p.IsOpen) return "The port is closed.";
            try { lock (p) p.Write(data, 0, data.Length); return null; }
            catch (Exception ex) { Error = ex.Message; return ex.Message; }
        }

        // the port disappeared (USB unplugged)?
        public bool Gone()
        {
            return port != null && Array.IndexOf(SerialPort.GetPortNames(), Name) < 0;
        }

        void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var p = (SerialPort)sender;
                string s = p.ReadExisting();
                Interlocked.Add(ref Bytes, s.Length);
                lock (part)
                {
                    foreach (char c in s)
                    {
                        // binary records (bit 7 set in the first byte; the text lines of an older FPGA are 7-bit)
                        if (!isBin && c >= 0x80) { isBin = true; part.Clear(); }
                        if (isBin)
                        {
                            int se = bin.SyncErr;
                            binOut.Clear();
                            bin.Feed(c & 0xFF, binOut);
                            if (bin.SyncErr != se)
                            {
                                Interlocked.Increment(ref UartErr);
                                Q.Enqueue(new RxLine(DateTime.Now, "# uart error sync (record cut)"));
                            }
                            foreach (string l in binOut) Q.Enqueue(new RxLine(DateTime.Now, l));
                            if (binOut.Count > 0 && bin.SyncErr == se) LastRx = DateTime.Now;
                            continue;
                        }
                        if (c == '\n')
                        {
                            string l = part.ToString().TrimEnd('\r');
                            part.Clear();
                            // the port may open in the middle of a line: a first line without its time stamp is only the rest of it
                            if (first)
                            {
                                first = false;
                                if (!Regex.IsMatch(l, "^[0-9A-F]{8} "))
                                {
                                    Interlocked.Increment(ref Partial);
                                    Q.Enqueue(new RxLine(DateTime.Now, "# partial " + l));
                                    continue;
                                }
                            }
                            Q.Enqueue(new RxLine(DateTime.Now, l));
                            LastRx = DateTime.Now;
                        }
                        else if (part.Length < 65536) part.Append(c);
                    }
                }
            }
            catch (Exception ex) { Error = ex.Message; }
        }
    }

    // raw lines saved while connected: <logs>/yyyyMMdd_HHmmss_<port>.log
    class AutoSave
    {
        StreamWriter w;
        public string Path = "";

        public bool Open(string portName)
        {
            try
            {
                Directory.CreateDirectory(Settings.LogDir);
                string safe = Regex.Replace(portName, @"[^A-Za-z0-9]+", "_").Trim('_');
                Path = System.IO.Path.Combine(Settings.LogDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + safe + ".log");
                w = new StreamWriter(Path, false, Encoding.Latin1);
                return true;
            }
            catch (Exception) { w = null; return false; }
        }

        public void Write(string l)
        {
            if (w == null) return;
            try { w.WriteLine(l); } catch (Exception) { Close(); }
        }

        public void Flush()
        {
            try { if (w != null) w.Flush(); } catch (Exception) { }
        }

        public void Close()
        {
            try { if (w != null) w.Dispose(); } catch (Exception) { }
            w = null;
        }

        public bool Active { get { return w != null; } }
    }
}
