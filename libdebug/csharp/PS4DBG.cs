using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace libdebug
{
    public partial class PS4DBG
    {
        private Socket sock = null;
        private IPEndPoint enp = null;

        public bool IsConnected { get; private set; } = false;

        public bool IsDebugging { get; private set; } = false;

        public string Version { get; private set; } = "";

        public int ExtFWVersion { get; private set; } = 0;

        private Thread debugThread = null;

        // some global values
        private const string LIBRARY_VERSION = "1.2.4";
        private const int PS4DBG_PORT = 744;
        private const int PS4DBG_DEBUG_PORT = 755;
        private const int NET_MAX_LENGTH = 0x20000; // 128kb

        private const int BROADCAST_PORT = 1010;
        private const uint BROADCAST_MAGIC = 0xFFFFAAAA;

        // from protocol.h
        // each packet starts with the magic
        // each C# base type can translate into a packet field
        // some packets, such as write take an additional data whose length will be specified in the cmd packet data field structure specific to that cmd type
        // ushort - 2 bytes | uint - 4 bytes | ulong - 8 bytes
        private const uint CMD_PACKET_MAGIC = 0xFFAABBCC;

        // from debug.h
        //struct debug_breakpoint {
        //    uint32_t valid;
        //    uint64_t address;
        //    uint8_t original;
        //};
        public static uint MAX_BREAKPOINTS = 10;
        public static uint MAX_WATCHPOINTS = 4;

        //  struct cmd_packet {
        //    uint32_t magic;
        //    uint32_t cmd;
        //    uint32_t datalen;
        //    // (field not actually part of packet, comes after)
        //    uint8_t* data;
        //  }
        //  __attribute__((packed));
        //  #define CMD_PACKET_SIZE 12
        private const int CMD_PACKET_SIZE = 12;
        public enum CMDS : uint
        {
            CMD_VERSION = 0xBD000001,
            CMD_EXT_FW_VERSION = 0xBD000500,

            CMD_PROC_LIST = 0xBDAA0001,
            CMD_PROC_READ = 0xBDAA0002,
            CMD_PROC_WRITE = 0xBDAA0003,
            CMD_PROC_MAPS = 0xBDAA0004,
            CMD_PROC_INTALL = 0xBDAA0005,
            CMD_PROC_CALL = 0xBDAA0006,
            CMD_PROC_ELF = 0xBDAA0007,
            CMD_PROC_PROTECT = 0xBDAA0008,
            CMD_PROC_SCAN = 0xBDAA0009,
            CMD_PROC_INFO = 0xBDAA000A,
            CMD_PROC_ALLOC = 0xBDAA000B,
            CMD_PROC_FREE = 0xBDAA000C,

            CMD_DEBUG_ATTACH = 0xBDBB0001,
            CMD_DEBUG_DETACH = 0xBDBB0002,
            CMD_DEBUG_BREAKPT = 0xBDBB0003,
            CMD_DEBUG_WATCHPT = 0xBDBB0004,
            CMD_DEBUG_THREADS = 0xBDBB0005,
            CMD_DEBUG_STOPTHR = 0xBDBB0006,
            CMD_DEBUG_RESUMETHR = 0xBDBB0007,
            CMD_DEBUG_GETREGS = 0xBDBB0008,
            CMD_DEBUG_SETREGS = 0xBDBB0009,
            CMD_DEBUG_GETFPREGS = 0xBDBB000A,
            CMD_DEBUG_SETFPREGS = 0xBDBB000B,
            CMD_DEBUG_GETDBGREGS = 0xBDBB000C,
            CMD_DEBUG_SETDBGREGS = 0xBDBB000D,
            CMD_DEBUG_STOPGO = 0xBDBB0010,
            CMD_DEBUG_THRINFO = 0xBDBB0011,
            CMD_DEBUG_SINGLESTEP = 0xBDBB0012,
            CMD_DEBUG_EXT_STOPGO = 0xBDBB0500,

            CMD_KERN_BASE = 0xBDCC0001,
            CMD_KERN_READ = 0xBDCC0002,
            CMD_KERN_WRITE = 0xBDCC0003,

            CMD_CONSOLE_REBOOT = 0xBDDD0001,
            CMD_CONSOLE_END = 0xBDDD0002,
            CMD_CONSOLE_PRINT = 0xBDDD0003,
            CMD_CONSOLE_NOTIFY = 0xBDDD0004,
            CMD_CONSOLE_INFO = 0xBDDD0005,
        };

        public enum CMD_STATUS : uint
        {
            CMD_SUCCESS = 0x80000000,
            CMD_ERROR = 0xF0000001,
            CMD_TOO_MUCH_DATA = 0xF0000002,
            CMD_DATA_NULL = 0xF0000003,
            CMD_ALREADY_DEBUG = 0xF0000004,
            CMD_INVALID_INDEX = 0xF0000005
        };

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct CMDPacket
        {
            public uint magic;
            public uint cmd;
            public uint datalen;
        }

        // enums
        public enum VM_PROTECTIONS : uint
        {
            VM_PROT_NONE = 0x00,
            VM_PROT_READ = 0x01,
            VM_PROT_WRITE = 0x02,
            VM_PROT_EXECUTE = 0x04,
            VM_PROT_DEFAULT = (VM_PROT_READ | VM_PROT_WRITE),
            VM_PROT_ALL = (VM_PROT_READ | VM_PROT_WRITE | VM_PROT_EXECUTE),
            VM_PROT_NO_CHANGE = 0x08,
            VM_PROT_COPY = 0x10,
            VM_PROT_WANTS_COPY = 0x10
        };

        public enum WATCHPT_LENGTH : uint
        {
            DBREG_DR7_LEN_1 = 0x00,	/* 1 byte length */
            DBREG_DR7_LEN_2 = 0x01,
            DBREG_DR7_LEN_4 = 0x03,
            DBREG_DR7_LEN_8 = 0x02,
        };

        public enum WATCHPT_BREAKTYPE : uint
        {
            DBREG_DR7_EXEC = 0x00,	/* break on execute       */
            DBREG_DR7_WRONLY = 0x01,	/* break on write         */
            DBREG_DR7_RDWR = 0x03,	/* break on read or write */
        };

        // General helper functions, make code cleaner
        public static string ConvertASCII(byte[] data, int offset)
        {
            int length = Array.IndexOf<byte>(data, 0, offset) - offset;
            if (length < 0)
            {
                length = data.Length - offset;
            }

            return Encoding.ASCII.GetString(data, offset, length);
        }

        public static byte[] SubArray(byte[] data, int offset, int length)
        {
            byte[] bytes = new byte[length];
            Buffer.BlockCopy(data, offset, bytes, 0, length);
            return bytes;
        }

        public static object GetObjectFromBytes(byte[] buffer, Type type)
        {
            int size = Marshal.SizeOf(type);

            IntPtr ptr = Marshal.AllocHGlobal(size);

            Marshal.Copy(buffer, 0, ptr, size);
            object r = Marshal.PtrToStructure(ptr, type);

            Marshal.FreeHGlobal(ptr);

            return r;
        }

        public static byte[] GetBytesFromObject(object obj)
        {
            int size = Marshal.SizeOf(obj);

            byte[] bytes = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);

            Marshal.StructureToPtr(obj, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);

            Marshal.FreeHGlobal(ptr);

            return bytes;
        }

        // General networking functions
        private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress subnetMask)
        {
            byte[] ipAdressBytes = address.GetAddressBytes();
            byte[] subnetMaskBytes = subnetMask.GetAddressBytes();

            byte[] broadcastAddress = new byte[ipAdressBytes.Length];
            for (int i = 0; i < broadcastAddress.Length; i++)
            {
                broadcastAddress[i] = (byte)(ipAdressBytes[i] | (subnetMaskBytes[i] ^ 255));
            }

            return new IPAddress(broadcastAddress);
        }

        private void SendCMDPacket(CMDS cmd, int length, params object[] fields)
        {
            CMDPacket packet = new CMDPacket
            {
                magic = CMD_PACKET_MAGIC,
                cmd = (uint) cmd,
                datalen = (uint) length
            };

            byte[] data = null;

            if (length > 0)
            {
                using (MemoryStream rs = new MemoryStream())
                {
                    foreach (object field in fields)
                    {
                        byte[] bytes = null;

                        switch (field)
                        {
                            case char c:
                                bytes = new byte[1];
                                bytes[0] = BitConverter.GetBytes(c)[0];
                                break;
                            case byte b:
                                bytes = new byte[1];
                                bytes[0] = BitConverter.GetBytes(b)[0];
                                break;
                            case short s:
                                bytes = BitConverter.GetBytes(s);
                                break;
                            case ushort us:
                                bytes = BitConverter.GetBytes(us);
                                break;
                            case int i:
                                bytes = BitConverter.GetBytes(i);
                                break;
                            case uint u:
                                bytes = BitConverter.GetBytes(u);
                                break;
                            case long l:
                                bytes = BitConverter.GetBytes(l);
                                break;
                            case ulong ul:
                                bytes = BitConverter.GetBytes(ul);
                                break;
                            case byte[] ba:
                                bytes = ba;
                                break;
                        }

                        if (bytes != null) rs.Write(bytes, 0, bytes.Length);
                        data = rs.ToArray();
                    }
                }
            }

            SendData(GetBytesFromObject(packet), CMD_PACKET_SIZE);

            if (data != null) SendData(data, length);
        }

        private void SendData(byte[] data, int length)
        {
            int left = length;
            int offset = 0;
            int sent = 0;

            while (left > 0)
            {
                if (left > NET_MAX_LENGTH)
                {
                    byte[] bytes = SubArray(data, offset, NET_MAX_LENGTH);
                    sent = sock.Send(bytes, NET_MAX_LENGTH, SocketFlags.None);
                }
                else
                {
                    byte[] bytes = SubArray(data, offset, left);
                    sent = sock.Send(bytes, left, SocketFlags.None);
                }

                offset += sent;
                left -= sent;
            }
        }

        private byte[] ReceiveData(int length)
        {
            MemoryStream s = new MemoryStream();

            int left = length;
            int recv = 0;
            byte[] b = new byte[NET_MAX_LENGTH];
            while (left > 0)
            {
                // adhere to length
                recv = sock.Receive(b, Math.Min(left, NET_MAX_LENGTH), SocketFlags.None);
                s.Write(b, 0, recv);
                left -= recv;
            }

            byte[] data = s.ToArray();

            s.Dispose();
            //GC.Collect();

            return data;
        }

        private CMD_STATUS ReceiveStatus()
        {
            byte[] status = new byte[4];
            sock.Receive(status, 4, SocketFlags.None);
            return (CMD_STATUS)BitConverter.ToUInt32(status, 0);
        }

        private void CheckStatus()
        {
            CMD_STATUS status = ReceiveStatus();
            if (status != CMD_STATUS.CMD_SUCCESS)
                throw new Exception($"libdbg status 0x{(uint)status:X} {Enum.GetName(typeof(CMD_STATUS), status)}");
        }

        private void CheckConnected()
        {
            if (!IsConnected) throw new Exception("libdbg: not connected");
        }

        private void CheckDebugging()
        {
            if (!IsDebugging) throw new Exception("libdbg: not debugging");
        }

        /// <summary>
        /// Initializes PS4DBG class
        /// </summary>
        /// <param name="addr">PlayStation 4 address</param>
        public PS4DBG(IPAddress addr)
        {
            enp = new IPEndPoint(addr, PS4DBG_PORT);
            sock = new Socket(enp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        /// <summary>
        /// Initializes PS4DBG class
        /// </summary>
        /// <param name="ip">PlayStation 4 ip address</param>
        public PS4DBG(string ip)
        {
            IPAddress addr;
            try
            {
                addr = IPAddress.Parse(ip);
            }
            catch (FormatException ex)
            {
                throw ex;
            }

            enp = new IPEndPoint(addr, PS4DBG_PORT);
            sock = new Socket(enp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        /// <summary>
        /// Find the playstation ip
        /// </summary>
        public static string FindPlayStation(int timeout = 100, string subnet_mask = "255.255.255.0")
        {
            UdpClient uc = new UdpClient();
            IPEndPoint server = new IPEndPoint(IPAddress.Any, 0);
            uc.EnableBroadcast = true;
            uc.Client.ReceiveTimeout = timeout;

            byte[] magic = BitConverter.GetBytes(BROADCAST_MAGIC);

            IPAddress addr = null;
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (IPAddress ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    addr = ip;
                    try
                    {
                        uc.Send(magic, magic.Length, new IPEndPoint(GetBroadcastAddress(addr, IPAddress.Parse(subnet_mask)), BROADCAST_PORT));

                        byte[] resp = uc.Receive(ref server);
                        if (BitConverter.ToUInt32(resp, 0) == BROADCAST_MAGIC)
                            return server.Address.ToString();
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Wrong IP, trying next one...");
                    }
                }
            }

            return "";
        }

        /// <summary>
        /// Connects to PlayStation 4
        /// </summary>
        public bool Connect(int connectTimeout = 1000 * 10, int sendTimeout = 1000 * 10, int receiveTimeout = 1000 * 10, bool getVersion = false)
        {
            if (!IsConnected || !sock.Connected)
            {
                sock.NoDelay = true;
                sock.ReceiveBufferSize = NET_MAX_LENGTH;
                sock.SendBufferSize = NET_MAX_LENGTH;

                sock.SendTimeout = sendTimeout;
                sock.ReceiveTimeout = receiveTimeout;

                try
                {
                    new Thread(new ThreadStart(() => {
                        Thread.Sleep(connectTimeout);
                        if (!sock.Connected) sock.Close();
                        Thread.CurrentThread.Abort();
                    })).Start();
                    sock.Connect(enp);

                    IsConnected = sock.Connected;
                    if (getVersion)
                    {
                        GetConsoleDebugVersion();
                        GetExtFWVersion();
                    }
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            return IsConnected;
        }

        /// <summary>
        /// Disconnects from PlayStation 4
        /// </summary>
        public bool Disconnect()
        {
            SendCMDPacket(CMDS.CMD_CONSOLE_END, 0);
            try
            {
                sock.Shutdown(SocketShutdown.Both);
                sock.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return false;
            }
            IsConnected = false;
            return true;
        }

        /// <summary>
        /// Get current ps4debug version from library
        /// </summary>
        public string GetLibraryDebugVersion() => LIBRARY_VERSION;

        /// <summary>
        /// Get the current ps4debug version from console
        /// </summary>
        public string GetConsoleDebugVersion()
        {
            if (Version != "") return Version;

            CheckConnected();

            SendCMDPacket(CMDS.CMD_VERSION, 0);

            byte[] ldata = new byte[4];
            sock.Receive(ldata, 4, SocketFlags.None);

            int length = BitConverter.ToInt32(ldata, 0);

            byte[] data = new byte[length];
            sock.Receive(data, length, SocketFlags.None);

            Version = ConvertASCII(data, 0);
            return Version;
        }
    }
}