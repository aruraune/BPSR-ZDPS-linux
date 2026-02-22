using System.Diagnostics;
using Serilog;

namespace BPSR_ZDPSLib;

public class Utils
{
    private static Dictionary<string, ProcessCacheEntry> ProcessCache = [];

    public static List<TcpConnectionProvider.TcpConnection> GetTCPConnectionsForExe(string[] filenames)
    {
        var sw = Stopwatch.StartNew();
        List<int> pids = [];
        
        Log.Information("[TCP] Looking for game processes: [{names}]", string.Join(", ", filenames));
        
        foreach (var filename in filenames)
        {
            if (ProcessCache.TryGetValue(filename, out var processCache))
            {
                // Check that it is still running
                if (TcpConnectionProvider.IsProcessAlive(processCache.ProcessId))
                {
                    pids.Add(processCache.ProcessId);
                    Log.Debug("[TCP] Using cached PID {pid} for {name}", processCache.ProcessId, filename);
                    continue;
                }
                else
                {
                    Log.Debug("[TCP] Cached process {name} (PID {pid}) is no longer alive", filename, processCache.ProcessId);
                    ProcessCache.Remove(filename);
                }
            }

            var procs = GetProcessesFromList(filenames);
            var process = procs.TryGetValue(filename, out var tempProcess) ? tempProcess : null;
            if (process != null)
            {
                ProcessCache.Add(filename, new ProcessCacheEntry()
                {
                    ProcessId = process.Id,
                    ProcessName = process.ProcessName,
                });

                pids.Add(process.Id);
                Log.Information("[TCP] Found game process: {name} (PID {pid})", filename, process.Id);
            }
        }
        
        if (pids.Count == 0)
        {
            sw.Stop();
            Log.Warning("[TCP] No target processes found! Searched for: [{names}] (took {time}ms)", 
                string.Join(", ", filenames), sw.ElapsedMilliseconds);
            return [];
        }

        var connections = TcpConnectionProvider.GetConnectionsForProcesses(pids);
        sw.Stop();
        
        if (connections.Count > 0)
        {
            Log.Information("[TCP] Found {count} connections for PIDs [{pids}] (took {time}ms)", 
                connections.Count, string.Join(", ", pids), sw.ElapsedMilliseconds);
            foreach (var conn in connections.Take(5))
            {
                Log.Debug("[TCP]   {local}:{localPort} <-> {remote}:{remotePort}",
                    conn.LocalAddress, conn.LocalPort, conn.RemoteAddress, conn.RemotePort);
            }
            if (connections.Count > 5)
                Log.Debug("[TCP]   ... and {more} more", connections.Count - 5);
        }
        else
        {
            Log.Warning("[TCP] No TCP connections found for PIDs [{pids}]", string.Join(", ", pids));
        }

        return connections;
    }

    public static Dictionary<string, Process> GetProcessesFromList(string[] filenames)
    {
        var processesDict = new Dictionary<string, Process>();
        var processes = Process.GetProcesses();
        
        // Create a set for faster lookup, including .exe variants for Wine/Proton compatibility
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in filenames)
        {
            targetNames.Add(name);
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                targetNames.Add(name + ".exe");
            }
        }
        
        foreach (var process in processes)
        {
            if (targetNames.Contains(process.ProcessName))
            {
                // Store with the original search name (without .exe) for consistency
                var normalizedName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? process.ProcessName.Substring(0, process.ProcessName.Length - 4)
                    : process.ProcessName;
                    
                processesDict.TryAdd(normalizedName, process);
                Log.Debug("[TCP] Found process: {procName} (PID {pid}) -> normalized to {normalized}",
                    process.ProcessName, process.Id, normalizedName);
            }
        }

        return processesDict;
    }

    public static ProcessCacheEntry? GetCachedProcessEntry()
    {
        return ProcessCache?.Values.FirstOrDefault();
    }

    /*
    public static void PrintExeTCPConnections(string filename = "BPSR")
    {
        Log.Information("TCP connections for {Filename}", filename);
        Log.Information("Pid, LocalAddress, LocalPort, RemoteAddress, RemotePort, State");
        foreach (var conn in GetTCPConnectionsForExe(filename)) {
            Log.Information("{Pid}, {LocalAddress}, {LocalPort}, {RemoteAddress}, {RemotePort}, {State}",
                conn.owningPid,
                conn.LocalAddress,
                conn.LocalPort,
                conn.RemoteAddress,
                conn.RemotePort,
                conn.state);
        }
    }
    */

    public class ProcessCacheEntry
    {
        public int ProcessId;
        public string ProcessName;
    }
}