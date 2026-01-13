namespace RazorLS.Server.VirtualDocuments;

/// <summary>
/// Represents a Razor document with its virtual HTML projection.
/// </summary>
public class VirtualDocument
{
    private readonly object _lock = new();
    private string _content = "";
    private string _htmlContent = "";
    private string? _htmlChecksum;
    private int _version;

    public VirtualDocument(string uri)
    {
        Uri = uri;
        VirtualHtmlUri = uri + "__virtual.html";
    }

    /// <summary>
    /// The URI of the original Razor document.
    /// </summary>
    public string Uri { get; }

    /// <summary>
    /// The URI of the virtual HTML document.
    /// </summary>
    public string VirtualHtmlUri { get; }

    /// <summary>
    /// The current content of the Razor document.
    /// </summary>
    public string Content
    {
        get { lock (_lock) return _content; }
    }

    /// <summary>
    /// The current HTML projection content.
    /// </summary>
    public string HtmlContent
    {
        get { lock (_lock) return _htmlContent; }
    }

    /// <summary>
    /// The checksum of the current HTML content.
    /// </summary>
    public string? HtmlChecksum
    {
        get { lock (_lock) return _htmlChecksum; }
    }

    /// <summary>
    /// The current document version.
    /// </summary>
    public int Version
    {
        get { lock (_lock) return _version; }
    }

    /// <summary>
    /// Language ID of the document.
    /// </summary>
    public string LanguageId { get; set; } = "razor";

    /// <summary>
    /// Updates the Razor document content.
    /// </summary>
    public void UpdateContent(string content, int version)
    {
        lock (_lock)
        {
            _content = content;
            _version = version;
        }
    }

    /// <summary>
    /// Updates the HTML projection from Roslyn.
    /// </summary>
    public bool UpdateHtmlContent(string checksum, string htmlContent)
    {
        lock (_lock)
        {
            // Only update if checksum is different (new content)
            if (_htmlChecksum == checksum)
            {
                return false;
            }

            _htmlContent = htmlContent;
            _htmlChecksum = checksum;
            return true;
        }
    }

    /// <summary>
    /// Validates that the given checksum matches the current HTML content.
    /// </summary>
    public bool ValidateChecksum(string checksum)
    {
        lock (_lock)
        {
            return _htmlChecksum == checksum;
        }
    }
}
