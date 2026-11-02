using System.IO;
using System.Text.Json.Nodes;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;
using ProJict.Core.Wiki;

namespace ProJict.App.Services.Sharing;

public sealed partial class HostConnection
{
    /// <summary>The host's wiki pages you may see: a copy kept in memory while connected, never saved here.</summary>
    public WikiBook? Wiki { get; internal set; }

    /// <summary>You may change its pages: you're an editor there (or it's you), and connected.</summary>
    public bool CanEditWiki => State == ConnectionState.Connected && (IsSelf || Role == GuestRole.Editor);
}

/// <summary>
/// The hosts' wikis, as a guest: their pages you may see (ones about projects shared with you, and ones they share with
/// everyone) arrive and stay up to date; what you change goes to the host, which checks it against your role, merges it
/// like its own edits and sends the page back changed (or says why not).
/// </summary>
public sealed partial class GuestService
{
    /// <summary>A host's wiki changed (UI thread).</summary>
    public event Action<HostConnection>? WikiChanged;

    private WikiBook WikiOf(HostConnection connection)
    {
        if (connection.Wiki is { } book) return book;
        book = new WikiBook([], () => connection.HostName);
        book.Changed += _ => WikiChanged?.Invoke(connection);
        book.Removed += _ => WikiChanged?.Invoke(connection);
        connection.Wiki = book;
        return book;
    }

    private static WikiPage? PageFrom(JsonNode? node) => node == null ? null : Json.Deserialize<WikiPage>(node.ToJsonString());

    private void HandleWiki(HostConnection connection, string type, JsonObject message)
    {
        switch (type)
        {
            case "wiki":
                var book = WikiOf(connection);
                var incoming = (message["pages"] as JsonArray ?? []).Select(PageFrom).OfType<WikiPage>().ToList();
                foreach (var gone in book.Pages.Where(p => incoming.All(i => i.Id != p.Id)).Select(p => p.Id).ToList()) book.Drop(gone);
                foreach (var page in incoming) book.Upsert(page);
                WikiChanged?.Invoke(connection);
                break;
            case "wikiPage":
                if (PageFrom(message["page"]) is { } changed) WikiOf(connection).Upsert(changed);
                break;
            case "wikiGone":
                if (Guid.TryParse((string?)message["id"], out var id)) connection.Wiki?.Drop(id);
                break;
            case "wikiError":
                _state.StatusText = $"{connection.HostName}'s ProJict: {(string?)message["message"]}";
                break;
        }
    }

    /// <summary>While disconnected the host's pages are gone; connecting again brings them all back.</summary>
    private void DropWiki(HostConnection connection)
    {
        if (connection.Wiki is not { } book) return;
        foreach (var id in book.Pages.Select(p => p.Id).ToList()) book.Drop(id);
    }

    /// <summary>The connection a page came from, or null for your own.</summary>
    public HostConnection? ConnectionOf(WikiPage page) => Connections.FirstOrDefault(c => c.Wiki?.Pages.Contains(page) == true);

    /// <summary>Something you (or, with <paramref name="agent"/>, your agent) do in a host's wiki; the host answers with the page, or why not.</summary>
    public void WikiOp(HostConnection connection, string op, string? agent, params (string Name, JsonNode? Value)[] fields)
    {
        var message = Wire.Message("wikiOp", fields);
        message["op"] = op;
        if (agent != null) message["as"] = agent;
        connection.Send(message);
    }

    /// <summary>Adds files to a host's page: the files go first, so the host has them when the page refers to them.</summary>
    public void WikiAttach(HostConnection connection, WikiPage page, IReadOnlyList<OutgoingFile> files) => _ = WikiAttachAsync(connection, page, files);

    private async Task WikiAttachAsync(HostConnection connection, WikiPage page, IReadOnlyList<OutgoingFile> files)
    {
        var sent = await PutFilesAsync(connection, files);
        if (sent.Count > 0) WikiOp(connection, "attach", null, ("page", page.Id.ToString()), ("files", sent));
    }

    /// <summary>Where a file of a host's page is kept once fetched (with the host's other files).</summary>
    public string WikiFilePath(HostConnection connection, Attachment attachment) =>
        Path.Combine(_state.RemoteCacheRoot, ProjectStore.SafeFileName(connection.Address), attachment.Id.ToString("N"), ProjectStore.SafeFileName(attachment.FileName));

    /// <summary>Asks the host for a page's file (once) when it isn't here yet.</summary>
    public void FetchWikiFile(HostConnection connection, Attachment attachment)
    {
        if (connection.State != ConnectionState.Connected || File.Exists(WikiFilePath(connection, attachment)) || !connection.Requested.Add(attachment.Id)) return;
        connection.Send(Wire.Message("getFile", ("id", attachment.Id.ToString())));
    }

    /// <summary>The file of a page in the host's wiki, if the id is one.</summary>
    private static Attachment? WikiAttachment(HostConnection connection, Guid id) =>
        connection.Wiki?.Pages.SelectMany(p => p.Attachments ?? []).FirstOrDefault(a => a.Id == id);
}
