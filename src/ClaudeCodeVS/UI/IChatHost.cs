namespace ClaudeCodeVS.UI
{
    /// <summary>Services the transcript elements need from the chat window.</summary>
    internal interface IChatHost
    {
        MarkdownRenderer Markdown { get; }

        void OpenLink(string target);

        void OpenFile(string path, int line);

        void InsertCode(string code);

        /// <summary>Resolves a path (relative to the working directory) or file link to an existing file, else null.</summary>
        string ResolveExistingFile(string pathOrLink, out int line);

        string ToDisplayPath(string path);
    }
}
