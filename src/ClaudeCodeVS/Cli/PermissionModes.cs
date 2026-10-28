namespace ClaudeCodeVS.Cli
{
    public enum PermissionModeOption
    {
        Default,
        AcceptEdits,
        Plan,
        Auto,
        BypassPermissions,
    }

    internal static class PermissionModes
    {
        public static readonly PermissionModeOption[] All =
        {
            PermissionModeOption.Default,
            PermissionModeOption.AcceptEdits,
            PermissionModeOption.Plan,
            PermissionModeOption.Auto,
            PermissionModeOption.BypassPermissions,
        };

        public static string ToCli(PermissionModeOption mode)
        {
            switch (mode)
            {
                case PermissionModeOption.AcceptEdits: return "acceptEdits";
                case PermissionModeOption.Plan: return "plan";
                case PermissionModeOption.Auto: return "auto";
                case PermissionModeOption.BypassPermissions: return "bypassPermissions";
                default: return "default";
            }
        }

        public static PermissionModeOption FromCli(string value)
        {
            foreach (var mode in All)
            {
                if (string.Equals(ToCli(mode), value, System.StringComparison.OrdinalIgnoreCase))
                {
                    return mode;
                }
            }

            return PermissionModeOption.Default;
        }

        public static string DisplayName(PermissionModeOption mode)
        {
            switch (mode)
            {
                case PermissionModeOption.AcceptEdits: return "Düzenlemeleri kabul et";
                case PermissionModeOption.Plan: return "Plan modu";
                case PermissionModeOption.Auto: return "Otomatik mod";
                case PermissionModeOption.BypassPermissions: return "İzinleri atla (tehlikeli)";
                default: return "Her işlemde sor";
            }
        }
    }
}
