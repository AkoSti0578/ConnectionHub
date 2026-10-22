using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace ConnectionHub.Ssh;

public sealed record RemoteItem(
    string Name,
    string FullName,
    bool IsDirectory,
    bool IsSymbolicLink,
    long Size,
    DateTime Modified,
    string Permissions)
{
    /// <summary>
    /// The listing entry. Deleting or renaming must go through it: SSH.NET resolves symlinks in path-based
    /// calls, so DeleteFile/RenameFile on a link path would act on the link's target instead.
    /// </summary>
    internal ISftpFile? Source { get; init; }
}

/// <summary>Progress of a transfer: bytes done / total for the current run.</summary>
public sealed record TransferProgress(string CurrentFile, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>SFTP file operations on top of SSH.NET, using the same login flow (incl. 2FA) as terminal sessions.</summary>
public sealed class SftpSession : IDisposable
{
    private readonly SshAuthenticator _auth;
    private SftpClient? _client;

    public SftpSession(SshConnectOptions options, ISshInteraction interaction)
    {
        _auth = new SshAuthenticator(options, interaction);
    }

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>The directory the server starts in (usually the user's home).</summary>
    public string HomeDirectory { get; private set; } = "/";

    public Task ConnectAsync() => Task.Run(() =>
    {
        _client = new SftpClient(_auth.CreateConnectionInfo())
        {
            OperationTimeout = TimeSpan.FromSeconds(60)
        };
        _auth.Connect(_client);
        HomeDirectory = _client.WorkingDirectory;
    });

    public Task<List<RemoteItem>> ListAsync(string path) => Task.Run(() =>
        Client.ListDirectory(path)
            .Where(f => f.Name is not "." and not "..")
            .Select(ToItem)
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    public Task<RemoteItem> StatAsync(string path) => Task.Run(() => ToItem(Client.Get(path)));

    public Task CreateDirectoryAsync(string path) => Task.Run(() => Client.CreateDirectory(path));

    public Task RenameAsync(RemoteItem item, string newName) => Task.Run(() =>
    {
        var target = Combine(Parent(item.FullName), newName);
        if (item.Source != null) item.Source.MoveTo(target);
        else if (!item.IsSymbolicLink) Client.RenameFile(item.FullName, target);
        else throw new InvalidOperationException("Ververs de lijst en probeer opnieuw.");
    });

    /// <summary>Deletes files and (recursively) directories.</summary>
    public Task DeleteAsync(IEnumerable<RemoteItem> items, CancellationToken token) => Task.Run(() =>
    {
        foreach (var item in items) DeleteRecursive(item, token);
    }, token);

    /// <summary>Uploads local files and folders into a remote directory.</summary>
    public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDirectory, IProgress<TransferProgress> progress, CancellationToken token)
        => Task.Run(() =>
        {
            var plan = new List<(string Local, string Remote)>();
            var directories = new List<string>();
            foreach (var path in localPaths)
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var target = Combine(remoteDirectory, name);
                if (Directory.Exists(path))
                {
                    directories.Add(target);
                    foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                        directories.Add(Combine(target, ToRemoteRelative(path, dir)));
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        plan.Add((file, Combine(target, ToRemoteRelative(path, file))));
                }
                else if (File.Exists(path))
                {
                    plan.Add((path, target));
                }
            }

            foreach (var dir in directories)
            {
                token.ThrowIfCancellationRequested();
                if (!Client.Exists(dir)) Client.CreateDirectory(dir);
            }

            var total = plan.Sum(p => new FileInfo(p.Local).Length);
            long done = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (local, remote) = plan[i];
                var fileName = Path.GetFileName(local);
                var before = done;
                using var stream = File.OpenRead(local);
                using var registration = token.Register(() => stream.Dispose());
                try
                {
                    Client.UploadFile(stream, remote, true, uploaded =>
                    {
                        done = before + (long)uploaded;
                        progress.Report(new TransferProgress(fileName, done, total, i, plan.Count));
                    });
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    TryDeleteRemote(remote);
                    throw new OperationCanceledException(token);
                }
                done = before + stream.Length;
                progress.Report(new TransferProgress(fileName, done, total, i + 1, plan.Count));
            }
        }, token);

    /// <summary>Downloads remote files and folders into a local directory.</summary>
    public Task DownloadAsync(IReadOnlyList<RemoteItem> items, string localDirectory, IProgress<TransferProgress> progress, CancellationToken token)
        => Task.Run(() =>
        {
            var plan = new List<(RemoteItem Remote, string Local)>();
            foreach (var item in items)
                Collect(item, Path.Combine(localDirectory, SafeLocalName(item.Name)), plan, token);

            var total = plan.Sum(p => p.Remote.Size);
            long done = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (remote, local) = plan[i];
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                var before = done;
                var completed = false;
                try
                {
                    using (var stream = File.Create(local))
                    using (token.Register(() => stream.Dispose()))
                    {
                        Client.DownloadFile(remote.FullName, stream, downloaded =>
                        {
                            done = before + (long)downloaded;
                            progress.Report(new TransferProgress(remote.Name, done, total, i, plan.Count));
                        });
                    }
                    completed = true;
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                finally
                {
                    if (!completed) TryDeleteLocal(local);
                }
                done = before + remote.Size;
                progress.Report(new TransferProgress(remote.Name, done, total, i + 1, plan.Count));
            }
        }, token);

    public void Disconnect()
    {
        try { if (_client?.IsConnected == true) _client.Disconnect(); }
        catch (Exception) { /* already gone */ }
    }

    public void Dispose()
    {
        Disconnect();
        _client?.Dispose();
    }

    public static string Combine(string directory, string name)
        => directory.EndsWith('/') ? directory + name : directory + "/" + name;

    public static string Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private SftpClient Client => _client is { IsConnected: true } c ? c : throw new InvalidOperationException("Niet verbonden met de SFTP-server.");

    private void Collect(RemoteItem item, string localPath, List<(RemoteItem, string)> plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.IsDirectory)
        {
            Directory.CreateDirectory(localPath);
            foreach (var child in Client.ListDirectory(item.FullName).Where(f => f.Name is not "." and not ".."))
                Collect(ToItem(child), Path.Combine(localPath, SafeLocalName(child.Name)), plan, token);
        }
        else
        {
            plan.Add((item, localPath));
        }
    }

    private void DeleteRecursive(RemoteItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.IsDirectory && !item.IsSymbolicLink)
        {
            foreach (var child in Client.ListDirectory(item.FullName).Where(f => f.Name is not "." and not ".."))
                DeleteRecursive(ToItem(child), token);
        }

        if (item.Source != null) item.Source.Delete();
        else if (!item.IsSymbolicLink) (item.IsDirectory ? (Action<string>)Client.DeleteDirectory : Client.DeleteFile)(item.FullName);
        else throw new InvalidOperationException($"'{item.Name}' kan niet veilig worden verwijderd; ververs de lijst en probeer opnieuw.");
    }

    private void TryDeleteRemote(string path)
    {
        try { if (_client?.IsConnected == true) _client.DeleteFile(path); }
        catch (Exception) { /* partial file stays */ }
    }

    private static void TryDeleteLocal(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* partial file stays */ }
    }

    /// <remarks>Directory listings describe a symlink itself; the view tries to open a symlink as a folder first.</remarks>
    private static RemoteItem ToItem(ISftpFile f)
        => new(f.Name, f.FullName, f.IsDirectory, f.IsSymbolicLink, f.IsDirectory ? 0 : f.Length, f.LastWriteTime, PermissionString(f)) { Source = f };

    private static string PermissionString(ISftpFile f)
    {
        var chars = new[]
        {
            f.IsDirectory ? 'd' : f.IsSymbolicLink ? 'l' : '-',
            f.OwnerCanRead ? 'r' : '-', f.OwnerCanWrite ? 'w' : '-', f.OwnerCanExecute ? 'x' : '-',
            f.GroupCanRead ? 'r' : '-', f.GroupCanWrite ? 'w' : '-', f.GroupCanExecute ? 'x' : '-',
            f.OthersCanRead ? 'r' : '-', f.OthersCanWrite ? 'w' : '-', f.OthersCanExecute ? 'x' : '-'
        };
        return new string(chars);
    }

    private static string ToRemoteRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Remote names may contain characters that Windows does not allow in file names.</summary>
    private static string SafeLocalName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        foreach (var c in new[] { '<', '>', ':', '"', '|', '?', '*', '\\' }) name = name.Replace(c, '_');
        return name;
    }
}
