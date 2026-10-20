using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectionHub.Models;

namespace ConnectionHub.Services;

/// <summary>Persists groups, connections and trusted SSH host keys in %APPDATA%\ConnectionHub.</summary>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();

    public ConnectionStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConnectionHub");
        FilePath = Path.Combine(Directory, "connections.json");
    }

    public string Directory { get; }
    public string FilePath { get; }
    public ConnectionDatabase Data { get; private set; } = new();

    public void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
            {
                Data = new ConnectionDatabase();
                return;
            }

            try
            {
                Data = JsonSerializer.Deserialize<ConnectionDatabase>(File.ReadAllText(FilePath), JsonOptions) ?? new ConnectionDatabase();
            }
            catch (JsonException)
            {
                // Keep a copy of the unreadable file instead of silently discarding it.
                File.Copy(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                Data = new ConnectionDatabase();
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    public void Export(string path, bool includePasswords)
    {
        var copy = JsonSerializer.Deserialize<ConnectionDatabase>(JsonSerializer.Serialize(Data, JsonOptions), JsonOptions)!;
        if (!includePasswords)
        {
            foreach (var c in copy.Connections)
            {
                c.ProtectedPassword = null;
                c.Ssh.ProtectedKeyPassphrase = null;
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(copy, JsonOptions));
    }

    /// <summary>Merges an exported file into the current database. Returns the number of imported connections.</summary>
    public int Import(string path)
    {
        var imported = JsonSerializer.Deserialize<ConnectionDatabase>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidDataException("Leeg of ongeldig bestand.");
        lock (_sync)
        {
            foreach (var g in imported.Groups.Where(g => Data.Groups.All(x => x.Id != g.Id)))
                Data.Groups.Add(g);
            var count = 0;
            foreach (var c in imported.Connections)
            {
                if (Data.Connections.Any(x => x.Id == c.Id)) c.Id = Guid.NewGuid();
                Data.Connections.Add(c);
                count++;
            }
            foreach (var kv in imported.KnownHosts)
                Data.KnownHosts.TryAdd(kv.Key, kv.Value);
            return count;
        }
    }
}
