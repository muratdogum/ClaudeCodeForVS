using ClaudeCodeVS.Cli;
using ClaudeCodeVS.Localization;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Options
{
    /// <summary>Tools > Options > Claude Code > General</summary>
    public sealed class GeneralOptionsPage : DialogPage
    {
        [LocalizedCategory("Claude Code CLI")]
        [LocalizedDisplayName("claude.exe yolu")]
        [LocalizedDescription("Boş bırakılırsa PATH, %USERPROFILE%\\.local\\bin, npm ve VS Code eklentisi klasörlerinde otomatik aranır.")]
        public string ClaudeExecutablePath { get; set; } = string.Empty;

        [LocalizedCategory("Claude Code CLI")]
        [LocalizedDisplayName("Model")]
        [LocalizedDescription("opus, sonnet, haiku, fable veya tam model kimliği. Boş bırakılırsa Claude Code'un varsayılan modeli kullanılır. Panelden de değiştirilebilir.")]
        public string Model { get; set; } = string.Empty;

        [LocalizedCategory("Claude Code CLI")]
        [LocalizedDisplayName("İzin modu")]
        [LocalizedDescription("Default: her işlemde sorar. AcceptEdits: dosya düzenlemelerini otomatik kabul eder. Plan: önce plan hazırlar. " +
                     "Auto: Claude Code'un otomatik modu. BypassPermissions: hiçbir izin sormaz (dikkatli kullanın).")]
        public PermissionModeOption PermissionMode { get; set; } = PermissionModeOption.Default;

        [LocalizedCategory("Claude Code CLI")]
        [LocalizedDisplayName("Ek argümanlar")]
        [LocalizedDescription("claude.exe'ye olduğu gibi eklenecek argümanlar. Örnek: --add-dir D:\\ortak")]
        public string AdditionalArguments { get; set; } = string.Empty;

        [LocalizedCategory("Davranış")]
        [LocalizedDisplayName("Göndermeden önce dosyaları kaydet")]
        [LocalizedDescription("Claude'un güncel içeriği görmesi için mesaj göndermeden önce kaydedilmemiş açık dosyaları kaydeder.")]
        public bool SaveAllBeforeSend { get; set; } = true;

        [LocalizedCategory("Davranış")]
        [LocalizedDisplayName("Aktif dosyayı bağlam olarak ekle")]
        [LocalizedDescription("Varsayılan olarak aktif dosyanın yolu ve seçili satırlar mesajla birlikte gönderilir.")]
        public bool IncludeActiveFile { get; set; } = true;

        [LocalizedCategory("Davranış")]
        [LocalizedDisplayName("Visual Studio sistem istemi")]
        [LocalizedDescription("Claude'a Visual Studio içinde çalıştığını ve tıklanabilir dosya bağlantıları kullanmasını söyleyen kısa bir sistem istemi ekler.")]
        public bool AppendIdeSystemPrompt { get; set; } = true;

        [LocalizedCategory("Davranış")]
        [LocalizedDisplayName("Arayüz dili")]
        [LocalizedDescription("Eklentinin arayüz dili. Otomatik seçildiğinde Visual Studio'nun dili kullanılır; desteklenmiyorsa İngilizce. " +
                        "Değişiklik hemen uygulanır; paneldeki eski mesajlar yazıldıkları dilde kalır.")]
        public UiLanguage Language { get; set; } = UiLanguage.Auto;

        [LocalizedCategory("Davranış")]
        [LocalizedDisplayName("Terminal bağlantısı (/ide)")]
        [LocalizedDescription("Terminalde çalışan Claude Code'un bu Visual Studio'ya bağlanmasını sağlar (VS Code'daki /ide gibi): Claude seçili kodu ve " +
                        "Hata Listesi'ni görür, önerdiği değişiklikler Visual Studio'nun diff penceresinde onayınıza sunulur. Bağlantı yalnızca bu " +
                        "bilgisayardan ve ~/.claude/ide altındaki kilit dosyasındaki anahtarla kabul edilir.")]
        public bool IdeConnection { get; set; } = true;

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("Kurumsal gizlilik modu")]
        [LocalizedDescription("Telemetri/hata raporu/geri bildirim gönderimini kapatır; veri yayınlayan araçları, ağ komutlarını ve korunan dosyaların okunmasını engeller; " +
                     "'İzinleri atla' ve 'Otomatik' modlarını kaldırır. Kod yine de yanıt üretmek için seçili model sağlayıcısına gönderilir.")]
        public bool PrivacyMode { get; set; } = true;

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("Web araçlarına izin ver")]
        [LocalizedDescription("Gizlilik modunda WebFetch/WebSearch varsayılan olarak engellidir (dış sitelere istek ve sorgu gönderirler).")]
        public bool AllowWebTools { get; set; }

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("MCP sunucularına izin ver")]
        [LocalizedDescription("Gizlilik modunda kullanıcı/proje MCP sunucuları (Slack, Gmail, harici API'ler…) yüklenmez.")]
        public bool AllowMcpServers { get; set; }

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("Beklenen kuruluş")]
        [LocalizedDescription("Şirket kuruluşunun adı veya kimliği (claude auth status'taki orgName/orgId). Doluysa ve Claude Code başka bir hesapla " +
                     "(ör. kişisel hesap) oturum açmışsa gizlilik modunda mesaj gönderilmez. Zorunlu kılmak için BT ekibi merkezi politikada forceLoginOrgUUID kullanmalıdır.")]
        public string ExpectedOrganization { get; set; } = string.Empty;

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("Ağ komutlarını engelle")]
        [LocalizedDescription("curl, wget, Invoke-WebRequest, Invoke-RestMethod, scp gibi ağa veri taşıyan komutları (Bash ve PowerShell) onay verilse bile engeller. " +
                     "Kapalıyken de her komut onayınızı bekler ve ağ komutları kartta uyarıyla gösterilir.")]
        public bool BlockNetworkCommands { get; set; } = true;

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("Korunan dosya desenleri")]
        [LocalizedDescription("Noktalı virgülle ayrılmış glob desenleri. Eşleşen dosyalar Claude tarafından okunamaz/düzenlenemez ve bağlam olarak eklenmez. " +
                     "Eğik çizgisiz desen her klasördeki dosya adına uyar (ör. .env, *.pfx); eğik çizgili desen yola uyar (ör. **/secrets/**).")]
        public string ProtectedFilePatterns { get; set; } = Privacy.PrivacyPolicy.DefaultProtectedPatterns;

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("Gizli bilgi taraması")]
        [LocalizedDescription("Göndermeden önce mesajı ve eklenen kodu API anahtarı, parola, bağlantı dizesi, özel anahtar gibi değerler için tarar ve maskelemeyi önerir.")]
        public bool ScanForSecrets { get; set; } = true;

        [LocalizedCategory("Gizlilik ve güvenlik")]
        [LocalizedDisplayName("Yerel gönderim günlüğü")]
        [LocalizedDescription("Modele gönderilen mesajları, eklenen dosyaları ve araçların eriştiği dosya/komutları yalnızca bu bilgisayarda %LOCALAPPDATA%\\ClaudeCodeVS\\audit altına kaydeder.")]
        public bool AuditLog { get; set; } = true;

        [LocalizedCategory("Model sağlayıcısı")]
        [LocalizedDisplayName("Sağlayıcı")]
        [LocalizedDescription("Anthropic: doğrudan Anthropic. Bedrock/Vertex/Foundry: modeli şirketinizin kendi bulut hesabı üzerinden kullanır. " +
                     "CorporateGateway: ANTHROPIC_BASE_URL ile kurumsal bir ağ geçidine yönlendirir. Gerekli bölge/proje bilgileri 'Ortam değişkenleri'ne yazılır.")]
        public Privacy.ModelProvider Provider { get; set; } = Privacy.ModelProvider.Anthropic;

        [LocalizedCategory("Model sağlayıcısı")]
        [LocalizedDisplayName("Ortam değişkenleri")]
        [LocalizedDescription("claude.exe'ye verilecek AD=DEĞER çiftleri, noktalı virgülle ayrılmış. Örnekler: AWS_REGION=eu-central-1; " +
                     "ANTHROPIC_VERTEX_PROJECT_ID=proje; CLOUD_ML_REGION=europe-west1; ANTHROPIC_FOUNDRY_RESOURCE=kaynak; ANTHROPIC_BASE_URL=https://llm-gateway.sirket.local")]
        public string EnvironmentVariables { get; set; } = string.Empty;

        /// <summary>Raised after the person presses OK on this page.</summary>
        internal static event System.Action<GeneralOptionsPage> Applied;

        /// <summary>The privacy rules for a Claude Code session started from Visual Studio (panel or terminal).</summary>
        internal Privacy.PrivacyPolicy CreatePrivacyPolicy() => new Privacy.PrivacyPolicy
        {
            Enabled = PrivacyMode,
            AllowWebTools = AllowWebTools,
            AllowMcpServers = AllowMcpServers,
            BlockNetworkCommands = BlockNetworkCommands,
            ScanSecrets = ScanForSecrets,
            AuditLog = AuditLog,
            Provider = Provider,
            ProtectedPatterns = Privacy.PrivacyPolicy.ParsePatterns(ProtectedFilePatterns),
            ExtraEnvironment = Privacy.PrivacyPolicy.ParseEnvironment(EnvironmentVariables),
        };

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                L.Initialize(Language.Code());
                Applied?.Invoke(this);
            }
        }
    }
}
