using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Serilog;

namespace BPSR_ZDPSLib;

/// <summary>
/// Cross-platform TCP connection provider
/// </summary>
public static class TcpConnectionProvider
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public struct TcpConnection
    {
        public string LocalAddress { get; set; }
        public int LocalPort { get; set; }
        public string RemoteAddress { get; set; }
        public int RemotePort { get; set; }
        public int ProcessId { get; set; }
        public uint State { get; set; }
    }

    /// <summary>
    /// Get all TCP connections for specific process IDs
    /// </summary>
    public static List<TcpConnection> GetConnectionsForProcesses(List<int> processIds)
    {
        if (IsWindows)
            return GetConnectionsWindows(processIds);
        else if (IsLinux)
            return GetConnectionsLinux(processIds);
        else
            throw new PlatformNotSupportedException("Only Windows and Linux are supported");
    }

    #region Windows Implementation

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int tcpTableLength, bool sort, int ipVersion, int tcpTableType, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint state;
        private uint localAddr;
        private byte localPort1, localPort2, localPort3, localPort4;
        private uint remoteAddr;
        private byte remotePort1, remotePort2, remotePort3, remotePort4;
        public int owningPid;
        public string LocalAddress { get { return new IPAddress(localAddr).ToString(); } }
        public string RemoteAddress { get { return new IPAddress(remoteAddr).ToString(); } }
        public int LocalPort { get { return (localPort1 << 8) + localPort2; } }
        public int RemotePort { get { return (remotePort1 << 8) + remotePort2; } }
    }

    private static List<TcpConnection> GetConnectionsWindows(List<int> processIds)
    {
        var connections = new List<TcpConnection>();
        var pidSet = new HashSet<int>(processIds);
        IntPtr tcpTablePtr = IntPtr.Zero;

        try
        {
            int tcpTableLength = 0;
            if (GetExtendedTcpTable(tcpTablePtr, ref tcpTableLength, false, 2, 5, 0) != 0)
            {
                tcpTablePtr = Marshal.AllocHGlobal(tcpTableLength);
                if (GetExtendedTcpTable(tcpTablePtr, ref tcpTableLength, false, 2, 5, 0) == 0)
                {
                    IntPtr currentPtr = tcpTablePtr + Marshal.SizeOf(typeof(uint));

                    for (int i = 0; i < tcpTableLength / Marshal.SizeOf(typeof(TcpRow)); i++)
                    {
                        var tcpRow = Marshal.PtrToStructure<TcpRow>(currentPtr);
                        currentPtr += Marshal.SizeOf(typeof(TcpRow));

                        if (tcpRow.RemoteAddress != "0.0.0.0" && pidSet.Contains(tcpRow.owningPid))
                        {
                            connections.Add(new TcpConnection
                            {
                                LocalAddress = tcpRow.LocalAddress,
                                LocalPort = tcpRow.LocalPort,
                                RemoteAddress = tcpRow.RemoteAddress,
                                RemotePort = tcpRow.RemotePort,
                                ProcessId = tcpRow.owningPid,
                                State = tcpRow.state
                            });
                        }
                    }
                }
            }
        }
        finally
        {
            if (tcpTablePtr != IntPtr.Zero)
                Marshal.FreeHGlobal(tcpTablePtr);
        }

        return connections;
    }

    #endregion

    #region Linux Implementation

    // TCP states from the Linux kernel
    private static readonly Dictionary<string, uint> TcpStates = new()
    {
        { "01", 1 },  // ESTABLISHED
        { "02", 2 },  // SYN_SENT
        { "03", 3 },  // SYN_RECV
        { "04", 4 },  // FIN_WAIT1
        { "05", 5 },  // FIN_WAIT2
        { "06", 6 },  // TIME_WAIT
        { "07", 7 },  // CLOSE
        { "08", 8 },  // CLOSE_WAIT
        { "09", 9 },  // LAST_ACK
        { "0A", 10 }, // LISTEN
        { "0B", 11 }, // CLOSING
    };

    private static List<TcpConnection> GetConnectionsLinux(List<int> processIds)
    {
        var connections = new List<TcpConnection>();
        var pidSet = new HashSet<int>(processIds);

        Log.Information("[Linux TCP] Searching for connections of PIDs: [{pids}]", string.Join(", ", processIds));

        try
        {
            // Read TCP connections from /proc/net/tcp (IPv4)
            if (File.Exists("/proc/net/tcp"))
            {
                ParseProcNetTcp("/proc/net/tcp", pidSet, connections);
            }
            else
            {
                Log.Error("[Linux TCP] /proc/net/tcp does not exist!");
            }

            // Also read from /proc/net/tcp6 (IPv6) if needed
            // if (File.Exists("/proc/net/tcp6"))
            // {
            //     ParseProcNetTcp6("/proc/net/tcp6", pidSet, connections);
            // }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Linux TCP] Failed to read TCP connections from /proc/net/tcp");
        }

        Log.Information("[Linux TCP] Found {count} connections for target PIDs", connections.Count);
        return connections;
    }

    private static void ParseProcNetTcp(string filePath, HashSet<int> targetPids, List<TcpConnection> connections)
    {
        var lines = File.ReadAllLines(filePath);
        
        // Skip header line
        for (int i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
                continue;

            try
            {
                // Format: sl local_address rem_address st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode
                // Example: 0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 12345

                var localAddress = fields[1].Split(':');
                var remoteAddress = fields[2].Split(':');
                var state = fields[3];
                var inode = fields[9];

                // Parse addresses and ports
                var localIp = ParseHexIp(localAddress[0]);
                var localPort = Convert.ToInt32(localAddress[1], 16);
                var remoteIp = ParseHexIp(remoteAddress[0]);
                var remotePort = Convert.ToInt32(remoteAddress[1], 16);

                // Skip if not connected to a remote host
                if (remoteIp == "0.0.0.0" || remotePort == 0)
                    continue;

                // Find the process ID that owns this socket
                var pid = FindProcessByInode(inode, targetPids);
                if (pid > 0 && targetPids.Contains(pid))
                {
                    connections.Add(new TcpConnection
                    {
                        LocalAddress = localIp,
                        LocalPort = localPort,
                        RemoteAddress = remoteIp,
                        RemotePort = remotePort,
                        ProcessId = pid,
                        State = TcpStates.TryGetValue(state, out var stateValue) ? stateValue : 0
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to parse line from /proc/net/tcp: {Line}", lines[i]);
            }
        }
    }

    private static string ParseHexIp(string hexIp)
    {
        // /proc/net/tcp stores IPs in little-endian hex format
        // Example: 0100007F = 127.0.0.1
        if (hexIp.Length == 8)
        {
            var b1 = Convert.ToByte(hexIp.Substring(6, 2), 16);
            var b2 = Convert.ToByte(hexIp.Substring(4, 2), 16);
            var b3 = Convert.ToByte(hexIp.Substring(2, 2), 16);
            var b4 = Convert.ToByte(hexIp.Substring(0, 2), 16);
            return $"{b1}.{b2}.{b3}.{b4}";
        }
        return "0.0.0.0";
    }

    private static int FindProcessByInode(string inode, HashSet<int> targetPids)
    {
        // Only check target PIDs for performance
        foreach (var pid in targetPids)
        {
            var fdPath = $"/proc/{pid}/fd";
            if (!Directory.Exists(fdPath))
                continue;

            try
            {
                var fds = Directory.GetFiles(fdPath);
                foreach (var fd in fds)
                {
                    try
                    {
                        var target = new System.IO.FileInfo(fd).LinkTarget;
                        if (target != null && target.Contains($"socket:[{inode}]"))
                        {
                            return pid;
                        }
                    }
                    catch
                    {
                        // Permission denied or broken symlink - skip
                    }
                }
            }
            catch
            {
                // Process might have terminated - skip
            }
        }

        return -1;
    }

    #endregion

    /// <summary>
    /// Check if a process is still alive (cross-platform)
    /// </summary>
    public static bool IsProcessAlive(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
