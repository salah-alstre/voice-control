using System.Net;
using System.Security.Cryptography;
using VoiceCommander.Core.Speech;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Speech;

public sealed class WhisperModelManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vc-whisper-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly FakeHttp _http = new();
    private readonly byte[] _smallPayload = Payload(1_300_000, 1);
    private readonly byte[] _basePayload = Payload(1_100_000, 2);
    private readonly WhisperModelInfo _small;
    private readonly WhisperModelInfo _base;
    private readonly WhisperModelManager _mgr;

    public WhisperModelManagerTests()
    {
        _small = new("whisper-small", "Small", "ggml-small.bin", "https://example.invalid/ggml-small.bin",
            _smallPayload.Length, Sha(_smallPayload), true);
        _base = new("whisper-base", "Base", "ggml-base.bin", "https://example.invalid/ggml-base.bin",
            _basePayload.Length, Sha(_basePayload), false);
        _mgr = new WhisperModelManager(_settings, new NullLog(), _http, new[] { _small, _base }, _dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Payload(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        b[0] = 0x6c; b[1] = 0x6d; b[2] = 0x67; b[3] = 0x67;   // ggml magic
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private void Place(WhisperModelInfo m, byte[] content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mgr.InstallPath(m), content);
    }

    private sealed class SyncProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();
        public Action<double>? OnReport { get; set; }
        public void Report(double value) { Values.Add(value); OnReport?.Invoke(value); }
    }

    // ---- install --------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_installs_only_after_size_and_hash_verify()
    {
        _http.Payload = _smallPayload;
        var progress = new SyncProgress();
        Assert.False(_mgr.IsInstalled(_small));

        await _mgr.DownloadAsync(_small, progress, CancellationToken.None);

        Assert.True(_mgr.IsInstalled(_small));
        Assert.Equal(_mgr.InstallPath(_small), _mgr.ResolveModelPath());
        Assert.False(File.Exists(_mgr.InstallPath(_small) + ".part"));
        Assert.Equal(_smallPayload, File.ReadAllBytes(_mgr.InstallPath(_small)));
        Assert.Equal(1.0, progress.Values.Last());
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);   // monotonic
    }

    [Fact]
    public async Task First_installed_model_becomes_the_selection()
    {
        _http.Payload = _basePayload;
        await _mgr.DownloadAsync(_base, null, CancellationToken.None);
        Assert.Equal("whisper-base", _mgr.Selected.Id);
        Assert.Equal("whisper-base", _settings.Current.WhisperModelId);
    }

    [Fact]
    public async Task Download_raises_Changed()
    {
        _http.Payload = _smallPayload;
        int changed = 0;
        _mgr.Changed += () => changed++;
        await _mgr.DownloadAsync(_small, null, CancellationToken.None);
        Assert.True(changed >= 1);
    }

    [Fact]
    public async Task Interrupted_download_resumes_from_the_part_file()
    {
        _http.Payload = _smallPayload;
        Directory.CreateDirectory(_dir);
        var part = _mgr.InstallPath(_small) + ".part";
        File.WriteAllBytes(part, _smallPayload[..500_000]);

        await _mgr.DownloadAsync(_small, null, CancellationToken.None);

        Assert.Equal(500_000, _http.RangeStarts[0]);
        Assert.True(_mgr.IsInstalled(_small));
        Assert.Equal(_smallPayload, File.ReadAllBytes(_mgr.InstallPath(_small)));
    }

    [Fact]
    public async Task Server_ignoring_the_range_restarts_cleanly()
    {
        _http.Payload = _smallPayload;
        _http.IgnoreRange = true;
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mgr.InstallPath(_small) + ".part", _smallPayload[..400_000]);

        await _mgr.DownloadAsync(_small, null, CancellationToken.None);

        Assert.Equal(_smallPayload, File.ReadAllBytes(_mgr.InstallPath(_small)));
    }

    [Fact]
    public async Task Range_not_satisfiable_discards_the_part_file_and_starts_over()
    {
        _http.Enqueue(new FakeHttp.Reply(HttpStatusCode.RequestedRangeNotSatisfiable, Array.Empty<byte>()));
        _http.Payload = _smallPayload;
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mgr.InstallPath(_small) + ".part", _smallPayload[..300_000]);

        await _mgr.DownloadAsync(_small, null, CancellationToken.None);

        Assert.Equal(2, _http.Requests.Count);
        Assert.Null(_http.RangeStarts[1]);
        Assert.Equal(_smallPayload, File.ReadAllBytes(_mgr.InstallPath(_small)));
    }

    [Fact]
    public async Task Oversized_part_file_is_discarded_instead_of_resumed()
    {
        _http.Payload = _smallPayload;
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_mgr.InstallPath(_small) + ".part", new byte[_smallPayload.Length + 10]);

        await _mgr.DownloadAsync(_small, null, CancellationToken.None);

        Assert.Null(_http.RangeStarts[0]);
        Assert.True(_mgr.IsInstalled(_small));
    }

    // ---- failure ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Wrong_size_is_rejected_and_never_marked_installed()
    {
        _http.Payload = _smallPayload[..^1000];
        var ex = await Assert.ThrowsAsync<WhisperModelException>(() => _mgr.DownloadAsync(_small, null, CancellationToken.None));
        Assert.Equal("model.corrupt", ex.Key);
        Assert.False(_mgr.IsInstalled(_small));
        Assert.False(File.Exists(_mgr.InstallPath(_small)));
        Assert.False(File.Exists(_mgr.InstallPath(_small) + ".part"));
    }

    [Fact]
    public async Task Same_size_but_wrong_content_fails_the_hash_check()
    {
        var tampered = (byte[])_smallPayload.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        _http.Payload = tampered;
        var ex = await Assert.ThrowsAsync<WhisperModelException>(() => _mgr.DownloadAsync(_small, null, CancellationToken.None));
        Assert.Equal("model.corrupt", ex.Key);
        Assert.False(_mgr.IsInstalled(_small));
        Assert.False(File.Exists(_mgr.InstallPath(_small) + ".part"));
    }

    [Fact]
    public async Task Network_failure_maps_to_a_friendly_key()
    {
        _http.ThrowOnSend = new HttpRequestException("offline");
        var ex = await Assert.ThrowsAsync<WhisperModelException>(() => _mgr.DownloadAsync(_small, null, CancellationToken.None));
        Assert.Equal("model.network", ex.Key);
        Assert.False(_mgr.IsInstalled(_small));
    }

    [Fact]
    public async Task Http_error_status_maps_to_network()
    {
        _http.Enqueue(new FakeHttp.Reply(HttpStatusCode.InternalServerError, Array.Empty<byte>()));
        var ex = await Assert.ThrowsAsync<WhisperModelException>(() => _mgr.DownloadAsync(_small, null, CancellationToken.None));
        Assert.Equal("model.network", ex.Key);
    }

    [Fact]
    public async Task Insufficient_disk_space_is_reported_before_downloading()
    {
        var huge = _small with { SizeBytes = long.MaxValue / 4 };
        var mgr = new WhisperModelManager(_settings, new NullLog(), _http, new[] { huge }, _dir);
        var ex = await Assert.ThrowsAsync<WhisperModelException>(() => mgr.DownloadAsync(huge, null, CancellationToken.None));
        Assert.Equal("model.disk-space", ex.Key);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Cancel_keeps_the_part_file_and_a_retry_resumes_it()
    {
        _http.Payload = _smallPayload;
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress { OnReport = v => { if (v > 0.2) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _mgr.DownloadAsync(_small, progress, cts.Token));

        var part = _mgr.InstallPath(_small) + ".part";
        Assert.True(File.Exists(part));
        Assert.False(_mgr.IsInstalled(_small));
        long kept = new FileInfo(part).Length;
        Assert.InRange(kept, 1, _smallPayload.Length - 1);

        await _mgr.DownloadAsync(_small, null, CancellationToken.None);
        Assert.Equal(kept, _http.RangeStarts.Last());
        Assert.True(_mgr.IsInstalled(_small));
    }

    [Fact]
    public async Task Concurrent_download_of_the_same_model_is_refused()
    {
        _http.Payload = _smallPayload;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _http.BeforeBody = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };

        var first = Task.Run(() => _mgr.DownloadAsync(_small, null, CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var ex = await Assert.ThrowsAsync<WhisperModelException>(() => _mgr.DownloadAsync(_small, null, CancellationToken.None));
        Assert.Equal("model.busy", ex.Key);

        release.Set();
        await first;
        Assert.True(_mgr.IsInstalled(_small));
    }

    // ---- validation -------------------------------------------------------------------------------------

    [Fact]
    public void ValidateQuick_reports_missing_tiny_and_wrong_header()
    {
        Directory.CreateDirectory(_dir);
        Assert.Equal("model.file-missing", _mgr.ValidateQuick(Path.Combine(_dir, "nope.bin")));

        var tiny = Path.Combine(_dir, "tiny.bin");
        File.WriteAllBytes(tiny, new byte[100]);
        Assert.Equal("model.file-invalid", _mgr.ValidateQuick(tiny));

        var noMagic = Path.Combine(_dir, "nomagic.bin");
        File.WriteAllBytes(noMagic, new byte[2_000_000]);
        Assert.Equal("model.file-invalid", _mgr.ValidateQuick(noMagic));

        var good = Path.Combine(_dir, "custom.bin");
        File.WriteAllBytes(good, _smallPayload);
        Assert.Null(_mgr.ValidateQuick(good));
    }

    [Fact]
    public void A_catalog_file_with_the_wrong_length_is_a_size_mismatch()
    {
        Place(_small, _smallPayload[..^5]);
        Assert.Equal("model.size-mismatch", _mgr.ValidateQuick(_mgr.InstallPath(_small)));
        Assert.False(_mgr.IsInstalled(_small));
        Assert.Null(_mgr.ResolveModelPath());
    }

    [Fact]
    public async Task ValidateAsync_detects_a_corrupt_file_of_the_right_size()
    {
        var bad = (byte[])_smallPayload.Clone();
        bad[bad.Length - 10] ^= 0x55;
        Place(_small, bad);
        Assert.Equal("model.corrupt", await _mgr.ValidateAsync(_mgr.InstallPath(_small), null, CancellationToken.None));

        Place(_small, _smallPayload);
        Assert.Null(await _mgr.ValidateAsync(_mgr.InstallPath(_small), null, CancellationToken.None));
    }

    // ---- selection / resolution ---------------------------------------------------------------------------

    [Fact]
    public void Missing_model_resolves_to_null()
    {
        Assert.Null(_mgr.ResolveModelPath());
    }

    [Fact]
    public void Selected_model_is_preferred_and_others_are_a_fallback()
    {
        Place(_small, _smallPayload);
        Place(_base, _basePayload);

        _mgr.Select("whisper-base");
        Assert.Equal(_mgr.InstallPath(_base), _mgr.ResolveModelPath());

        _mgr.Remove(_base);
        Assert.False(_mgr.IsInstalled(_base));
        Assert.Equal(_mgr.InstallPath(_small), _mgr.ResolveModelPath());   // falls back to what is installed
    }

    [Fact]
    public void Select_ignores_unknown_ids_and_persists_known_ones()
    {
        _mgr.Select("whisper-nonsense");
        Assert.Equal("whisper-small", _settings.Current.WhisperModelId);

        int changed = 0;
        _mgr.Changed += () => changed++;
        _mgr.Select("whisper-base");
        _mgr.Select("whisper-base");   // no-op the second time
        Assert.Equal("whisper-base", _settings.Current.WhisperModelId);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Custom_file_overrides_the_catalog_and_can_be_cleared()
    {
        Place(_small, _smallPayload);
        var custom = Path.Combine(_dir, "my-model.bin");
        File.WriteAllBytes(custom, _basePayload);

        Assert.Null(_mgr.UseFile(custom));
        Assert.Equal(custom, _mgr.CustomPath);
        Assert.Equal(custom, _mgr.ResolveModelPath());

        _mgr.ClearCustom();
        Assert.Null(_mgr.CustomPath);
        Assert.Equal(_mgr.InstallPath(_small), _mgr.ResolveModelPath());
    }

    [Fact]
    public void Invalid_custom_file_is_rejected_without_changing_settings()
    {
        Directory.CreateDirectory(_dir);
        var bad = Path.Combine(_dir, "bad.bin");
        File.WriteAllBytes(bad, new byte[4096]);
        Assert.Equal("model.file-invalid", _mgr.UseFile(bad));
        Assert.Null(_settings.Current.WhisperModelPath);
    }

    [Fact]
    public void A_custom_file_that_later_disappears_falls_back_to_the_catalog()
    {
        Place(_small, _smallPayload);
        var custom = Path.Combine(_dir, "my-model.bin");
        File.WriteAllBytes(custom, _basePayload);
        _mgr.UseFile(custom);
        File.Delete(custom);
        Assert.Equal(_mgr.InstallPath(_small), _mgr.ResolveModelPath());
    }

    [Fact]
    public void Remove_deletes_the_model_and_any_part_file()
    {
        Place(_small, _smallPayload);
        File.WriteAllBytes(_mgr.InstallPath(_small) + ".part", new byte[10]);
        _mgr.Remove(_small);
        Assert.False(File.Exists(_mgr.InstallPath(_small)));
        Assert.False(File.Exists(_mgr.InstallPath(_small) + ".part"));
    }

    [Fact]
    public void Default_models_directory_is_outside_the_project_and_application_folder()
    {
        using var sb = new Sandbox();
        var mgr = new WhisperModelManager(_settings, new NullLog());
        Assert.StartsWith(sb.Root, mgr.ModelsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.False(mgr.ModelsDirectory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Default_catalog_is_complete_and_pinned()
    {
        var c = WhisperModelManager.DefaultCatalog;
        Assert.Equal(new[] { "whisper-tiny", "whisper-base", "whisper-small", "whisper-medium" }, c.Select(m => m.Id));
        Assert.Single(c, m => m.Recommended);
        Assert.Equal("whisper-small", c.Single(m => m.Recommended).Id);
        Assert.All(c, m =>
        {
            Assert.StartsWith("https://huggingface.co/", m.Url);
            Assert.Equal(64, m.Sha256.Length);
            Assert.EndsWith(".bin", m.FileName);
        });
    }
}
