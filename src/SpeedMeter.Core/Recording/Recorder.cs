using SpeedMeter.Core.Sampling;

namespace SpeedMeter.Core.Recording;

/// <summary>
/// Persists the sampling loop to disk, so speed history outlives the process.
/// </summary>
/// <remarks>
/// <para>Two files, because the two halves are worth different amounts. Raw
/// one-second samples are 1.38 MB a day and only interesting while recent -
/// they carry burst shape and sub-minute stalls - so they are pruned after a
/// fortnight. Minute rollups are 57 KB a day, carry the max of the seconds
/// inside them, and are kept forever. Carrying the max is what makes the small
/// file enough: a peak query over a year never needs the seconds.</para>
/// <para>This class writes the rollup itself, so the history depends on
/// nothing else being installed or scheduled.</para>
/// <para>Nothing here may throw into the sampling loop. Every I/O failure
/// downgrades what is recorded, is noted once in error.log and raised once as
/// <see cref="Faulted"/>; the meter itself keeps running.</para>
/// </remarks>
public sealed class Recorder : IDisposable
{
    /// <summary>
    /// Windows longer than this are not attributed to a second. A resume from
    /// sleep can hand over one "sample" spanning an hour: the rate stays honest,
    /// but filing the hour's bytes against one second would invent a peak that
    /// never happened. Such a window is recorded as a flagged gap with no bytes.
    /// </summary>
    private const double MaxWindowSeconds = 3.0;

    private readonly string _minuteFolder;
    private readonly string _rawFolder;
    private readonly long _activeThresholdBps;
    private readonly int _rawRetentionDays;

    private bool _enabled;
    private bool _rawEnabled;
    private bool _faultReported;

    // One flush per minute, so the buffer holds at most one minute's samples.
    // Generous against timer drift; filling it early just flushes early.
    private readonly byte[] _rawBuffer = new byte[128 * LogFormat.RawRecordBytes];
    private int _rawBufferBytes;

    private long _minuteIndex = -1;
    private ulong _minuteDown;
    private ulong _minuteUp;
    private uint _minuteMaxDown;
    private uint _minuteMaxUp;
    private int _minuteSamples;
    private int _minuteActiveSamples;
    private int _minuteFlags;
    private byte _minuteAdapter;

    private readonly Dictionary<string, byte> _adapterIndex = new(StringComparer.OrdinalIgnoreCase);
    private byte _nextAdapterIndex = 1;
    private byte _lastAdapter = LogFormat.AdapterNone;
    private string? _prunedForDate;
    private bool _firstMinuteWritten;

    public Recorder(MeterSettings settings)
    {
        _activeThresholdBps = settings.ActiveThresholdBps;
        _rawRetentionDays = settings.RawRetentionDays;
        _minuteFolder = (settings.LogFolder ?? "").Trim();
        _rawFolder = (settings.RawFolder ?? "").Trim();

        _enabled = settings.Record && _minuteFolder.Length > 0 && EnsureFolder(_minuteFolder);

        // The halves fail independently, and not symmetrically. Losing raw costs
        // burst detail for a session; losing the rollup costs history for good.
        // Raw with nowhere to graduate into is writes buying nothing, so it is
        // never recorded alone.
        _rawEnabled = _enabled && _rawFolder.Length > 0 && EnsureFolder(_rawFolder);

        if (settings.Record && !_enabled)
            Fault($"creating {(_minuteFolder.Length > 0 ? _minuteFolder : "the log folder (not set)")} — recording is off this session", null);

        if (_enabled)
        {
            LoadAdapterTable();
            PruneRawFiles(DateTime.Now);
        }
    }

    /// <summary>False when nothing is being written, for callers that want to say so.</summary>
    public bool Enabled => _enabled;

    /// <summary>Whether one-second samples are being kept as well as minutes.</summary>
    public bool RawEnabled => _rawEnabled;

    public string MinuteFolder => _minuteFolder;

    /// <summary>The first failure of the session, in a line. Raised on the sampling thread.</summary>
    public event Action<string>? Faulted;

    /// <summary>A minute record reached the disk. Raised on the sampling thread.</summary>
    public event Action? MinuteWritten;

    /// <summary>The first failure of the session, if any.</summary>
    public string? FaultMessage { get; private set; }

    /// <summary>Files a reading. Called once per tick from the sampling loop.</summary>
    public void Add(Reading reading) => Add(reading, DateTime.UtcNow);

    internal void Add(Reading reading, DateTime nowUtc)
    {
        if (!_enabled) return;
        try
        {
            var seconds = (long)(nowUtc - DateTime.UnixEpoch).TotalSeconds;
            var minute = seconds / 60;

            if (_minuteIndex >= 0 && minute != _minuteIndex)
            {
                Flush();
                // The flush is the one place a write failure can stop recording, and it
                // must not then accumulate a minute that will never be written.
                if (!_enabled) return;
            }

            if (_minuteIndex < 0)
            {
                _minuteIndex = minute;
                _minuteAdapter = LogFormat.AdapterNone;
            }

            var adapter = IndexForAdapter(reading.SourceId, reading.SourceName);
            var gap = reading.ElapsedSeconds > MaxWindowSeconds;

            var flags = 0;
            if (reading.Connected) flags |= LogFormat.FlagConnected;
            if (gap) flags |= LogFormat.FlagGap;

            // "auto" follows the busiest adapter, so without this the history would
            // silently splice Wi-Fi and Ethernet into one series.
            if (adapter != _lastAdapter)
            {
                flags |= LogFormat.FlagAdapterChanged;
                if (_minuteSamples > 0) _minuteFlags |= LogFormat.FlagAdapterChanged;
            }
            _lastAdapter = adapter;

            var down = gap ? 0 : ToUInt32(reading.DownBytes);
            var up = gap ? 0 : ToUInt32(reading.UpBytes);
            WriteRaw(seconds, down, up, reading.ElapsedSeconds, adapter, flags, nowUtc);

            _minuteSamples++;
            _minuteAdapter = adapter;
            _minuteFlags |= flags & (LogFormat.FlagConnected | LogFormat.FlagGap);

            if (!gap)
            {
                _minuteDown += down;
                _minuteUp += up;
                var downRate = ToUInt32(reading.DownBytesPerSecond);
                var upRate = ToUInt32(reading.UpBytesPerSecond);
                if (downRate > _minuteMaxDown) _minuteMaxDown = downRate;
                if (upRate > _minuteMaxUp) _minuteMaxUp = upRate;
                if (downRate + (double)upRate >= _activeThresholdBps) _minuteActiveSamples++;
            }
        }
        catch (Exception ex)
        {
            Fault("recording a sample", ex);
        }
    }

    /// <summary>
    /// Writes the buffered minute out. Called at every minute boundary and on
    /// shutdown, so a clean exit keeps its partial minute and a crash costs at
    /// most the current one.
    /// </summary>
    public void Flush()
    {
        if (!_enabled || _minuteIndex < 0 || _minuteSamples == 0)
        {
            _rawBufferBytes = 0;
            _minuteIndex = -1;
            return;
        }
        WriteMinute(_minuteIndex);
    }

    public void Dispose()
    {
        try
        {
            Flush();
        }
        catch (Exception)
        {
            // Shutdown is not the place to raise anything.
        }
    }

    private void WriteMinute(long minute)
    {
        Span<byte> record = stackalloc byte[LogFormat.MinuteRecordBytes];
        record.Clear();
        LogFormat.WriteU32(record, 0, (uint)minute);
        LogFormat.WriteU64(record, 4, _minuteDown);
        LogFormat.WriteU64(record, 12, _minuteUp);
        LogFormat.WriteU32(record, 20, _minuteMaxDown);
        LogFormat.WriteU32(record, 24, _minuteMaxUp);
        LogFormat.WriteU16(record, 28, (ushort)Math.Min(_minuteSamples, ushort.MaxValue));
        LogFormat.WriteU16(record, 30, (ushort)Math.Min(_minuteActiveSamples, ushort.MaxValue));
        record[32] = _minuteAdapter;
        var flags = _minuteFlags;
        if (!_firstMinuteWritten)
        {
            flags |= LogFormat.FlagMeterStarted;
            _firstMinuteWritten = true;
        }
        record[33] = (byte)flags;
        // 34..39 stay zero: room for fields this format does not have yet.

        var local = DateTime.UnixEpoch.AddMinutes(minute).ToLocalTime();

        // Every buffered sample belongs to this one minute, and a day boundary is
        // always a minute boundary, so a batch never straddles two daily files.
        if (_rawBufferBytes > 0)
            Append(Path.Combine(_rawFolder, LogFormat.RawFileName(local)), _rawBuffer.AsSpan(0, _rawBufferBytes), essential: false);

        var written = Append(Path.Combine(_minuteFolder, LogFormat.MinuteFileName(local)), record, essential: true);

        _rawBufferBytes = 0;
        _minuteIndex = -1;
        _minuteDown = 0;
        _minuteUp = 0;
        _minuteMaxDown = 0;
        _minuteMaxUp = 0;
        _minuteSamples = 0;
        _minuteActiveSamples = 0;
        _minuteFlags = 0;

        PruneRawFiles(DateTime.Now);
        if (written) MinuteWritten?.Invoke();
    }

    private void WriteRaw(long seconds, uint down, uint up, double elapsedSeconds, byte adapter, int flags, DateTime nowUtc)
    {
        if (!_rawEnabled) return;
        if (_rawBufferBytes + LogFormat.RawRecordBytes > _rawBuffer.Length)
        {
            // Only reachable if a minute produced more samples than the cap.
            // Spilling early keeps the write bounded; the records stay in order.
            Append(Path.Combine(_rawFolder, LogFormat.RawFileName(nowUtc.ToLocalTime())), _rawBuffer.AsSpan(0, _rawBufferBytes), essential: false);
            _rawBufferBytes = 0;
        }

        var ms = elapsedSeconds * 1000.0;
        var elapsedMs = ms >= ushort.MaxValue ? ushort.MaxValue : (ushort)ms;
        var at = _rawBufferBytes;
        var span = _rawBuffer.AsSpan();
        LogFormat.WriteU32(span, at, (uint)seconds);
        LogFormat.WriteU32(span, at + 4, down);
        LogFormat.WriteU32(span, at + 8, up);
        LogFormat.WriteU16(span, at + 12, elapsedMs);
        _rawBuffer[at + 14] = adapter;
        _rawBuffer[at + 15] = (byte)flags;
        _rawBufferBytes += LogFormat.RawRecordBytes;
    }

    /// <summary>
    /// Appends, sharing the file for reading so the dashboard can follow a file
    /// still being written. A failure on the raw file disables raw; a failure on
    /// the minute file stops recording, since the point of the exercise is gone.
    /// </summary>
    private bool Append(string path, ReadOnlySpan<byte> bytes, bool essential)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            stream.Write(bytes);
            return true;
        }
        catch (Exception ex)
        {
            if (essential)
            {
                _enabled = false;
                _rawEnabled = false;
                Fault($"writing {path} — recording stopped for this session", ex);
            }
            else
            {
                _rawEnabled = false;
                Fault($"writing {path} — raw samples disabled, minute history continues", ex);
            }
            return false;
        }
    }

    /// <summary>Maps an adapter id to the byte stored on every record, learning ids as they appear.</summary>
    private byte IndexForAdapter(string? id, string name)
    {
        if (string.IsNullOrEmpty(id)) return LogFormat.AdapterNone;
        if (_adapterIndex.TryGetValue(id, out var index)) return index;
        // 255 adapters on one machine is not a real scenario, but wrapping would
        // silently relabel history, so unknown is the safer answer.
        if (_nextAdapterIndex == LogFormat.AdapterNone) return LogFormat.AdapterNone;

        index = _nextAdapterIndex;
        _adapterIndex[id] = index;
        _nextAdapterIndex = (byte)(_nextAdapterIndex == byte.MaxValue ? LogFormat.AdapterNone : _nextAdapterIndex + 1);
        try
        {
            AdapterTable.Append(_minuteFolder, index, id, name);
        }
        catch (Exception ex)
        {
            // The records still carry the index; only the name is lost, and it is
            // relearned the next time this adapter appears with a writable table.
            _adapterIndex.Remove(id);
            Fault("writing " + Path.Combine(_minuteFolder, LogFormat.AdapterTableName), ex);
        }
        return index;
    }

    private void LoadAdapterTable()
    {
        try
        {
            foreach (var row in AdapterTable.Read(_minuteFolder))
            {
                _adapterIndex[row.Id] = row.Index;
                if (row.Index >= _nextAdapterIndex)
                    _nextAdapterIndex = (byte)(row.Index == byte.MaxValue ? LogFormat.AdapterNone : row.Index + 1);
            }
        }
        catch (Exception ex)
        {
            // An empty table would hand out indices already in use and corrupt the
            // meaning of existing history, so recording stops instead.
            _enabled = false;
            _rawEnabled = false;
            Fault("reading " + Path.Combine(_minuteFolder, LogFormat.AdapterTableName), ex);
        }
    }

    /// <summary>
    /// Deletes raw files past the retention window, at most once per local day.
    /// Only files whose name parses as the exact date format are touched: a
    /// wildcard delete in a folder a user can point at anything is not worth it.
    /// </summary>
    private void PruneRawFiles(DateTime nowLocal)
    {
        if (!_rawEnabled || _rawRetentionDays <= 0) return;
        var today = nowLocal.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (_prunedForDate == today) return;
        _prunedForDate = today;
        try
        {
            var cutoff = nowLocal.Date.AddDays(-_rawRetentionDays);
            foreach (var path in Directory.GetFiles(_rawFolder, "*.bin"))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var stamp)) continue;
                if (stamp < cutoff) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Disk fills slower than history is worth; a failed prune is not worth stopping for.
            Fault("pruning " + _rawFolder, ex);
        }
    }

    private static bool EnsureFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Notes the first failure of the session and then stays quiet: a recorder
    /// that cannot write has usually lost a whole drive, and a line an hour for
    /// a week helps nobody.
    /// </summary>
    private void Fault(string what, Exception? ex)
    {
        if (_faultReported) return;
        _faultReported = true;
        FaultMessage = what;
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDir);
            File.AppendAllText(AppPaths.ErrorLog,
                $"{DateTime.Now:u}  recorder: {what}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
        try
        {
            Faulted?.Invoke(what);
        }
        catch (Exception)
        {
        }
    }

    private static uint ToUInt32(double value) => value <= 0 ? 0 : value >= uint.MaxValue ? uint.MaxValue : (uint)value;

    private static uint ToUInt32(long value) => value <= 0 ? 0 : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
}
