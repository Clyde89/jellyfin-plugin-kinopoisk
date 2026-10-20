#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kinopoisk.Services;

/// <summary>Личный список сохранён в данных плагина отдельно для каждого пользователя.</summary>
public sealed class KinopoiskWishlistStore
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public KinopoiskWishlistStore(string directory) { _directory = directory; }

    public async Task<IReadOnlyList<KinopoiskWishlistItem>> Read(Guid userId, CancellationToken token)
    {
        var gate = Gate(userId);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ReadFile(userId, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task Set(Guid userId, KinopoiskWishlistItem item, bool saved, CancellationToken token)
    {
        item.Validate(saved);
        var gate = Gate(userId);
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var items = (await ReadFile(userId, token).ConfigureAwait(false)).ToList();
            var previous = items.Find(existing => existing.Matches(item));
            items.RemoveAll(existing => existing.Matches(item));
            if (saved)
            {
                if (items.Count >= 500) throw new InvalidOperationException("В желаемом уже сохранено 500 записей.");
                item.AddedUtc = previous?.AddedUtc ?? DateTimeOffset.UtcNow;
                items.Insert(0, item);
            }
            Directory.CreateDirectory(_directory);
            var target = Filename(userId);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, items, Json, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (new FileInfo(temporary).Length > 4 * 1024 * 1024)
                throw new InvalidOperationException("Достигнут предел размера желаемого. Удалите ненужные записи.");
            token.ThrowIfCancellationRequested();
            if (File.Exists(target)) File.Copy(target, target + ".previous", true);
            File.Move(temporary, target, true);
            temporary = null;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }

    private SemaphoreSlim Gate(Guid userId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("Требуется пользователь Jellyfin.");
        return _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
    }
    private string Filename(Guid userId) => Path.Combine(_directory, userId.ToString("N") + ".json");
    private async Task<IReadOnlyList<KinopoiskWishlistItem>> ReadFile(Guid userId, CancellationToken token)
    {
        var path = Filename(userId);
        if (!File.Exists(path)) return Array.Empty<KinopoiskWishlistItem>();
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Список желаемого повреждён.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var items = JsonSerializer.Deserialize<List<KinopoiskWishlistItem>>(bytes, Json)
            ?? throw new InvalidDataException("Список желаемого повреждён.");
        if (items.Count > 500) throw new InvalidDataException("Список желаемого повреждён.");
        foreach (var item in items) item.Validate(true);
        return items;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class KinopoiskWishlistItem
{
    public int KinopoiskId { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie";
    public string Name { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string Overview { get; set; } = string.Empty;
    public string PosterUrl { get; set; } = string.Empty;
    public int? Year { get; set; }
    public double? RatingKinopoisk { get; set; }
    public DateTimeOffset AddedUtc { get; set; }
    [JsonIgnore] public string Key => MediaType + ":" + (KinopoiskId > 0 ? "kp:" + KinopoiskId : "tmdb:" + TmdbId);
    public bool Matches(KinopoiskWishlistItem other) => MediaType == other.MediaType
        && ((KinopoiskId > 0 && KinopoiskId == other.KinopoiskId)
            || (TmdbId > 0 && TmdbId == other.TmdbId && (KinopoiskId == 0 || other.KinopoiskId == 0)));
    public void Validate(bool metadata)
    {
        if (MediaType is not ("movie" or "tv") || KinopoiskId < 0 || TmdbId < 0 || KinopoiskId + (long)TmdbId < 1
            || (metadata && (string.IsNullOrWhiteSpace(Name) || Name.Length > 300 || OriginalName is null || OriginalName.Length > 300
                || Overview is null || Overview.Length > 4000 || PosterUrl is null || PosterUrl.Length > 2048
                || Year is < 1800 or > 2500 || RatingKinopoisk is < 0 or > 10)))
            throw new ArgumentException("Некорректная запись желаемого.");
    }
    public static KinopoiskWishlistItem ParseKey(string key)
    {
        var parts = key.Split(':');
        if (parts.Length != 3 || parts[1] is not ("kp" or "tmdb") || !int.TryParse(parts[2], out var id) || id < 1)
            throw new ArgumentException("Некорректный идентификатор фильма.");
        var item = new KinopoiskWishlistItem { MediaType = parts[0], KinopoiskId = parts[1] == "kp" ? id : 0, TmdbId = parts[1] == "tmdb" ? id : 0 };
        item.Validate(false);
        return item;
    }
}
