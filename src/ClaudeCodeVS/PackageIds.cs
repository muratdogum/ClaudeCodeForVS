using System;

namespace ClaudeCodeVS
{
    /// <summary>Must stay in sync with the Symbols section of ClaudeCodePackage.vsct.</summary>
    internal static class PackageGuids
    {
        public const string PackageString = "07cb461e-31c2-4301-8fe4-15963318e944";
        public const string CommandSetString = "036e1797-21af-431f-b48e-491727d946e5";
        public const string ToolWindowString = "519fa47a-17d3-46bd-87e6-e3876f9de31b";

        public static readonly Guid CommandSet = new Guid(CommandSetString);
    }

    internal static class PackageIds
    {
        public const int OpenClaudeCommand = 0x0100;
        public const int AddSelectionCommand = 0x0101;
        public const int ExplainSelectionCommand = 0x0102;
        public const int OpenTerminalCommand = 0x0103;
    }
}
