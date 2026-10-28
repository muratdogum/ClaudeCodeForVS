using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.ToolWindows
{
    [Guid(PackageGuids.ToolWindowString)]
    public sealed class ClaudeToolWindow : ToolWindowPane
    {
        public const string Title = "Claude Code";

        /// <summary>Created through the package's async tool window factory, which passes the package in.</summary>
        public ClaudeToolWindow(ClaudeCodePackage package)
            : base(null)
        {
            Caption = Title;
            BitmapImageMoniker = KnownMonikers.MessageBubble;
            Chat = new ClaudeChatControl(package);
            Content = Chat;
        }

        public ClaudeChatControl Chat { get; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Chat.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
