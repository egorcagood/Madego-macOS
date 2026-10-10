using System.Text.Json;

namespace EgorVpn.Services;

public sealed class AtomicJsonStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string? LoadWarning { get; private set; }
    public AtomicJsonStore(string path) => _path = path;

    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new T();
            try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(_path, ct)) ?? new T(); }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                LoadWarning = "Не удалось прочитать сохранённые данные. Оригинальный файл сохранён для восстановления.";
                try { File.Copy(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false); } catch (IOException) { }
                if (File.Exists(_path + ".bak"))
                {
                    try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(_path + ".bak", ct)) ?? new T(); }
                    catch (Exception backupEx) when (backupEx is JsonException or IOException) { }
                }
                return new T();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        // Snapshot before yielding: UI collections cannot mutate during serialization.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        await _gate.WaitAsync(ct);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllBytesAsync(temp, bytes, ct);
            if (File.Exists(_path)) File.Copy(_path, _path + ".bak", true);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            _gate.Release();
        }
    }
}
