using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RazorLS.Server.VirtualDocuments;

/// <summary>
/// Manages virtual documents for Razor files.
/// </summary>
public class VirtualDocumentManager
{
    private readonly ILogger<VirtualDocumentManager> _logger;
    private readonly ConcurrentDictionary<string, VirtualDocument> _documents = new();

    public event EventHandler<HtmlContentUpdatedEventArgs>? HtmlContentUpdated;

    public VirtualDocumentManager(ILogger<VirtualDocumentManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or creates a virtual document for the given URI.
    /// </summary>
    public VirtualDocument GetOrCreate(string uri)
    {
        return _documents.GetOrAdd(uri, static u => new VirtualDocument(u));
    }

    /// <summary>
    /// Tries to get an existing virtual document.
    /// </summary>
    public bool TryGet(string uri, out VirtualDocument? document)
    {
        return _documents.TryGetValue(uri, out document);
    }

    /// <summary>
    /// Opens a document with initial content.
    /// </summary>
    public VirtualDocument Open(string uri, string languageId, int version, string content)
    {
        var doc = GetOrCreate(uri);
        doc.LanguageId = languageId;
        doc.UpdateContent(content, version);
        _logger.LogDebug("Opened document: {Uri} (version {Version})", uri, version);
        return doc;
    }

    /// <summary>
    /// Updates a document's content.
    /// </summary>
    public bool Update(string uri, int version, string content)
    {
        if (!_documents.TryGetValue(uri, out var doc))
        {
            _logger.LogWarning("Attempted to update unknown document: {Uri}", uri);
            return false;
        }

        doc.UpdateContent(content, version);
        _logger.LogDebug("Updated document: {Uri} (version {Version})", uri, version);
        return true;
    }

    /// <summary>
    /// Closes a document.
    /// </summary>
    public bool Close(string uri)
    {
        if (_documents.TryRemove(uri, out _))
        {
            _logger.LogDebug("Closed document: {Uri}", uri);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Updates the HTML content for a Razor document.
    /// Called when we receive razor/updateHtml from Roslyn.
    /// </summary>
    public void UpdateHtmlContent(string uri, string checksum, string htmlContent)
    {
        var doc = GetOrCreate(uri);
        if (doc.UpdateHtmlContent(checksum, htmlContent))
        {
            _logger.LogDebug("Updated HTML content for {Uri} (checksum: {Checksum})", uri, checksum);
            HtmlContentUpdated?.Invoke(this, new HtmlContentUpdatedEventArgs(uri, checksum, htmlContent));
        }
    }

    /// <summary>
    /// Gets a document if the checksum matches.
    /// </summary>
    public VirtualDocument? GetWithValidChecksum(string uri, string checksum)
    {
        if (_documents.TryGetValue(uri, out var doc) && doc.ValidateChecksum(checksum))
        {
            return doc;
        }
        return null;
    }

    /// <summary>
    /// Gets all open documents.
    /// </summary>
    public IEnumerable<VirtualDocument> GetAllDocuments()
    {
        return _documents.Values;
    }

    /// <summary>
    /// Checks if the URI is a Razor document.
    /// </summary>
    public static bool IsRazorDocument(string uri)
    {
        return uri.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }
}

public class HtmlContentUpdatedEventArgs : EventArgs
{
    public HtmlContentUpdatedEventArgs(string uri, string checksum, string htmlContent)
    {
        Uri = uri;
        Checksum = checksum;
        HtmlContent = htmlContent;
    }

    public string Uri { get; }
    public string Checksum { get; }
    public string HtmlContent { get; }
}
