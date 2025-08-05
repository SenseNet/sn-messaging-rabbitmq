using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Tester;

/// <summary>
/// Advanced memory profiler that provides detailed memory usage information
/// for better memory leak detection in RabbitMQ message provider
/// </summary>
public class MemoryProfiler
{
    private readonly ILogger<MemoryProfiler>? _logger;
    private readonly Process _currentProcess;

    public MemoryProfiler(ILogger<MemoryProfiler>? logger = null)
    {
        _logger = logger;
        _currentProcess = Process.GetCurrentProcess();
    }

    public DetailedMemorySnapshot TakeSnapshot(string label)
    {
        // Force multiple garbage collections to get accurate managed memory
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _currentProcess.Refresh();

        var snapshot = new DetailedMemorySnapshot
        {
            Label = label,
            Timestamp = DateTime.Now,
            
            // Managed Memory
            ManagedMemoryBytes = GC.GetTotalMemory(false),
            
            // GC Information
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            
            // Process Memory
            WorkingSet = _currentProcess.WorkingSet64,
            PrivateMemorySize = _currentProcess.PrivateMemorySize64,
            VirtualMemorySize = _currentProcess.VirtualMemorySize64,
            PagedMemorySize = _currentProcess.PagedMemorySize64,
            NonpagedSystemMemorySize = _currentProcess.NonpagedSystemMemorySize64,
            PagedSystemMemorySize = _currentProcess.PagedSystemMemorySize64,
            
            // Thread and Handle Counts
            ThreadCount = _currentProcess.Threads.Count,
            HandleCount = _currentProcess.HandleCount,
            
            // System Memory Info
            AvailablePhysicalMemory = GetAvailablePhysicalMemory(),
            TotalPhysicalMemory = GetTotalPhysicalMemory()
        };

        _logger?.LogTrace($"Memory snapshot '{label}': Managed={snapshot.ManagedMemoryBytes / 1024 / 1024:F1}MB, " +
                         $"WorkingSet={snapshot.WorkingSet / 1024 / 1024:F1}MB, " +
                         $"Private={snapshot.PrivateMemorySize / 1024 / 1024:F1}MB, " +
                         $"Threads={snapshot.ThreadCount}, Handles={snapshot.HandleCount}");

        return snapshot;
    }

    public MemoryAnalysisResult AnalyzeMemoryTrend(List<DetailedMemorySnapshot> snapshots, string testDescription)
    {
        if (snapshots.Count < 2)
        {
            return new MemoryAnalysisResult
            {
                TestDescription = testDescription,
                HasLeaks = false,
                ErrorMessage = "Insufficient data for analysis (need at least 2 snapshots)"
            };
        }

        var first = snapshots.First();
        var last = snapshots.Last();
        var duration = last.Timestamp - first.Timestamp;

        var analysis = new MemoryAnalysisResult
        {
            TestDescription = testDescription,
            TestDuration = duration,
            SnapshotCount = snapshots.Count,
            
            // Calculate absolute changes
            ManagedMemoryChange = last.ManagedMemoryBytes - first.ManagedMemoryBytes,
            WorkingSetChange = last.WorkingSet - first.WorkingSet,
            PrivateMemoryChange = last.PrivateMemorySize - first.PrivateMemorySize,
            VirtualMemoryChange = last.VirtualMemorySize - first.VirtualMemorySize,
            ThreadCountChange = last.ThreadCount - first.ThreadCount,
            HandleCountChange = last.HandleCount - first.HandleCount,
            
            // Calculate percentage changes
            ManagedMemoryChangePercent = CalculatePercentageChange(first.ManagedMemoryBytes, last.ManagedMemoryBytes),
            WorkingSetChangePercent = CalculatePercentageChange(first.WorkingSet, last.WorkingSet),
            PrivateMemoryChangePercent = CalculatePercentageChange(first.PrivateMemorySize, last.PrivateMemorySize),
            
            // GC analysis
            TotalGCCollections = (last.Gen0Collections + last.Gen1Collections + last.Gen2Collections) -
                               (first.Gen0Collections + first.Gen1Collections + first.Gen2Collections),
            
            // Trend analysis
            ConsistentManagedMemoryGrowth = AnalyzeTrend(snapshots, s => s.ManagedMemoryBytes),
            ConsistentWorkingSetGrowth = AnalyzeTrend(snapshots, s => s.WorkingSet),
            ConsistentThreadGrowth = AnalyzeTrend(snapshots, s => s.ThreadCount),
            ConsistentHandleGrowth = AnalyzeTrend(snapshots, s => s.HandleCount)
        };

        // Detect leaks based on multiple criteria
        analysis.HasLeaks = DetectLeaks(analysis);
        analysis.LeakSeverity = DetermineLeakSeverity(analysis);
        analysis.Recommendations = GenerateRecommendations(analysis);

        return analysis;
    }

    private bool AnalyzeTrend(List<DetailedMemorySnapshot> snapshots, Func<DetailedMemorySnapshot, long> valueSelector)
    {
        if (snapshots.Count < 3) return false;

        var growthCount = 0;
        for (int i = 1; i < snapshots.Count; i++)
        {
            if (valueSelector(snapshots[i]) > valueSelector(snapshots[i - 1]))
                growthCount++;
        }

        // Consider it a consistent trend if more than 70% of measurements show growth
        return growthCount > (snapshots.Count - 1) * 0.7;
    }

    private bool DetectLeaks(MemoryAnalysisResult analysis)
    {
        // Multiple criteria for leak detection
        var leakIndicators = 0;

        // Significant managed memory growth
        if (analysis.ManagedMemoryChangePercent > 50 && analysis.ManagedMemoryChange > 10 * 1024 * 1024)
            leakIndicators++;

        // Significant working set growth
        if (analysis.WorkingSetChangePercent > 30 && analysis.WorkingSetChange > 20 * 1024 * 1024)
            leakIndicators++;

        // Thread or handle leaks
        if (analysis.ThreadCountChange > 5 || analysis.HandleCountChange > 100)
            leakIndicators++;

        // Consistent growth patterns
        if (analysis.ConsistentManagedMemoryGrowth && analysis.ConsistentWorkingSetGrowth)
            leakIndicators++;

        // Large private memory growth without corresponding managed memory growth (unmanaged leak)
        if (analysis.PrivateMemoryChange > 50 * 1024 * 1024 && analysis.ManagedMemoryChange < 10 * 1024 * 1024)
            leakIndicators++;

        return leakIndicators >= 2;
    }

    private LeakSeverity DetermineLeakSeverity(MemoryAnalysisResult analysis)
    {
        if (!analysis.HasLeaks) return LeakSeverity.None;

        var totalMemoryGrowth = analysis.WorkingSetChange;
        var percentGrowth = Math.Max(analysis.ManagedMemoryChangePercent, analysis.WorkingSetChangePercent);

        if (totalMemoryGrowth > 100 * 1024 * 1024 || percentGrowth > 100) // 100MB or 100% growth
            return LeakSeverity.Critical;
        
        if (totalMemoryGrowth > 50 * 1024 * 1024 || percentGrowth > 50) // 50MB or 50% growth
            return LeakSeverity.High;
        
        if (totalMemoryGrowth > 20 * 1024 * 1024 || percentGrowth > 25) // 20MB or 25% growth
            return LeakSeverity.Medium;
        
        return LeakSeverity.Low;
    }

    private List<string> GenerateRecommendations(MemoryAnalysisResult analysis)
    {
        var recommendations = new List<string>();

        if (analysis.HasLeaks)
        {
            if (analysis.ConsistentManagedMemoryGrowth)
                recommendations.Add("Check for object references not being released (event handlers, static collections)");
            
            if (analysis.ConsistentWorkingSetGrowth && !analysis.ConsistentManagedMemoryGrowth)
                recommendations.Add("Possible unmanaged memory leak - check P/Invoke calls and native resource disposal");
            
            if (analysis.ThreadCountChange > 0)
                recommendations.Add("Thread leak detected - ensure all threads are properly disposed");
            
            if (analysis.HandleCountChange > 50)
                recommendations.Add("Handle leak detected - check file/socket/timer disposal");
            
            recommendations.Add("Run longer tests to confirm leak patterns");
            recommendations.Add("Use memory profiler tools for detailed analysis");
        }
        else
        {
            recommendations.Add("No significant leaks detected in this test run");
            
            if (analysis.TestDuration.TotalMinutes < 5)
                recommendations.Add("Consider running longer tests for better leak detection");
        }

        return recommendations;
    }

    private static double CalculatePercentageChange(long initial, long final)
    {
        if (initial == 0) return final == 0 ? 0 : 100;
        return ((double)(final - initial) / initial) * 100;
    }

    private static long GetTotalPhysicalMemory()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Get total physical memory from GC
                return GC.GetTotalMemory(false);
            }
        }
        catch
        {
            // Fallback or other platforms
        }
        
        return 0; // Unable to determine
    }

    private static long GetAvailablePhysicalMemory()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Simple approximation - in a real scenario you might use WMI or other APIs
                return Environment.WorkingSet;
            }
        }
        catch
        {
            // Fallback for other platforms
        }
        
        return 0; // Unable to determine
    }

    public void Dispose()
    {
        _currentProcess?.Dispose();
    }
}

public class DetailedMemorySnapshot
{
    public required string Label { get; set; }
    public DateTime Timestamp { get; set; }
    
    // Managed Memory
    public long ManagedMemoryBytes { get; set; }
    
    // GC Information
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    
    // Process Memory
    public long WorkingSet { get; set; }
    public long PrivateMemorySize { get; set; }
    public long VirtualMemorySize { get; set; }
    public long PagedMemorySize { get; set; }
    public long NonpagedSystemMemorySize { get; set; }
    public long PagedSystemMemorySize { get; set; }
    
    // System Resources
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    
    // System Memory
    public long AvailablePhysicalMemory { get; set; }
    public long TotalPhysicalMemory { get; set; }
}

public class MemoryAnalysisResult
{
    public required string TestDescription { get; set; }
    public TimeSpan TestDuration { get; set; }
    public int SnapshotCount { get; set; }
    
    // Memory Changes
    public long ManagedMemoryChange { get; set; }
    public long WorkingSetChange { get; set; }
    public long PrivateMemoryChange { get; set; }
    public long VirtualMemoryChange { get; set; }
    
    // Percentage Changes
    public double ManagedMemoryChangePercent { get; set; }
    public double WorkingSetChangePercent { get; set; }
    public double PrivateMemoryChangePercent { get; set; }
    
    // Resource Changes
    public int ThreadCountChange { get; set; }
    public int HandleCountChange { get; set; }
    public int TotalGCCollections { get; set; }
    
    // Trend Analysis
    public bool ConsistentManagedMemoryGrowth { get; set; }
    public bool ConsistentWorkingSetGrowth { get; set; }
    public bool ConsistentThreadGrowth { get; set; }
    public bool ConsistentHandleGrowth { get; set; }
    
    // Leak Detection
    public bool HasLeaks { get; set; }
    public LeakSeverity LeakSeverity { get; set; }
    public List<string> Recommendations { get; set; } = new();
    public string ErrorMessage { get; set; } = string.Empty;
}

public enum LeakSeverity
{
    None,
    Low,
    Medium,
    High,
    Critical
}
