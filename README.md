# Claude Code for Visual Studio

Claude Code CLI'ını Visual Studio 2022 içinde bir sohbet paneli olarak kullanmanızı sağlayan, **resmi olmayan** bir eklenti.
Claude Code'un tüm ajan yeteneklerini kullanır: dosya okuma/düzenleme, komut çalıştırma, alt ajanlar, plan modu.
İzin istekleri paneldeki kartlarla onaylanır, önerilen değişiklikler Visual Studio'nun diff görünümünde incelenebilir.

## Gereksinimler

- Visual Studio 2022 (17.0+) veya Visual Studio 2026; Community, Professional ya da Enterprise
- Kurulu ve oturum açılmış **Claude Code**:
  ```powershell
  irm https://claude.ai/install.ps1 | iex   # kurulum
  claude                                    # ilk çalıştırmada /login ile giriş yapın
  ```
  Eklenti `claude.exe`'yi sırasıyla şu yerlerde arar: ayarlarda verilen yol, `PATH`, `%USERPROFILE%\.local\bin`, npm global
  kurulumu ve VS Code / Cursor eklentisiyle gelen kopya.

## Giriş (hesap)

Eklentinin ayrı bir girişi yoktur; bilgisayardaki **Claude Code girişini** kullanır (`%USERPROFILE%\.claude`). Bu giriş terminal ve
VS Code eklentisiyle ortaktır; birinde giriş yapmak yeterlidir.

- Üst çubuktaki **kişi ikonu** hangi hesapla bağlı olunduğunu gösterir (kuruluş, e-posta, giriş yöntemi, abonelik). Tıklayınca
  hesap kartı açılır: *Başka hesapla giriş*, *Çıkış yap*, *Yenile*.
- **Giriş yapılmamışsa** rozet turuncu *Giriş yap* olur, mesaj gönderilmez ve giriş kartı çıkar: *Claude hesabıyla giriş*
  (Pro/Max/Team/Enterprise), *SSO ile giriş*, *Console (API) ile giriş*. Butona basınca `claude auth login` bir pencerede başlar,
  tarayıcıda giriş tamamlanınca panel kendiliğinden güncellenir.
- Oturumun süresi dolarsa ya da Claude Code kimlik hatası verirse giriş kartı otomatik açılır.
- Bedrock/Vertex/Foundry seçiliyse Claude hesabı gerekmez; kimlik bilgileri bulut sağlayıcısından gelir.
- **Şirket hesabı kontrolü:** *Araçlar > Seçenekler > Claude Code > Beklenen kuruluş* alanına şirket kuruluşunun adını ya da
  kimliğini yazın. Başka bir hesapla (ör. kişisel hesap) giriş yapılmışsa gizlilik modunda mesaj gönderilmez. Kişisel
  aboneliklerde (Free/Pro/Max) hesap kartında uyarı gösterilir. Tüm makinelerde zorunlu kılmak için merkezi politikada
  `forceLoginOrgUUID` kullanın.

## Derleme ve kurulum

1. `ClaudeCodeVS.sln` dosyasını Visual Studio 2022'de açın. **Visual Studio extension development** iş yükü kurulu olmalıdır.
2. **F5** ile deneysel Visual Studio örneğinde çalıştırın ya da komut satırından derleyin:
   ```powershell
   & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" ClaudeCodeVS.sln /restore /p:Configuration=Release
   ```
3. Oluşan `src\ClaudeCodeVS\bin\Release\ClaudeCodeVS.vsix` dosyasına çift tıklayarak normal Visual Studio'ya kurun.

## Kullanım

- **Görünüm > Diğer Pencereler > Claude Code** veya **Araçlar > Claude Code** ile paneli açın.
- Mesaj yazıp **Enter**'a basın. **Shift+Enter** yeni satır ekler, **Esc** çalışan isteği durdurur, **↑** önceki mesajı getirir.
- **Geçmiş:** Üst çubuktaki *Geçmiş* butonu, bu klasördeki eski sohbetleri (Visual Studio, VS Code veya terminalde yapılmış olsun)
  Claude Code'un verdiği başlıklarla, güne göre gruplu ve aranabilir şekilde listeler. Birine tıklayınca konuşma ekrana yüklenir;
  yazdığınızda kaldığı yerden devam eder (`--resume`).
- **`@` ile dosya:** Mesaj yazarken `@` yazın; projedeki dosyalar aranabilir bir listede açılır (↑/↓, Enter/Tab, Esc).
  Seçilen dosyanın içeriğini Claude Code kendisi ekler. Gizlilik modunda korunan dosyalar listelenmez ve gönderilemez.
- **`/` komutları:** Mesaja `/` ile başlayınca `/compact`, `/context`, `/init`, `/review` ve kendi komutlarınız açıklamalarıyla listelenir.
- **Görseller:** Ekran görüntüsünü **Ctrl+V** ile yapıştırın ya da görsel dosyasını mesaj kutusuna sürükleyin (en fazla 10).
  Büyük görseller 1568 piksele küçültülür.
- **Bağlam göstergesi:** İlk yanıttan sonra mesaj kutusunun altında konuşmanın bağlam penceresini ne kadar doldurduğu (◔ %) görünür;
  %75'te turuncu, %90'da kırmızı olur. Tıklayınca konuşma özetlenip yer açılır (`/compact`).
- Her yanıtın altındaki kopyala ikonu Claude'un o turdaki yanıtını panoya kopyalar.
- Editörde sağ tık menüsünde iki komut var:
  - **Seçimi Claude'a Ekle**: seçili satırları bir sonraki mesaja ekler.
  - **Claude ile Açıkla**: seçimi hemen açıklatır.
- Panelin altındaki onay kutusu işaretliyse aktif dosya ve seçili satırlar mesajla birlikte gönderilir.
- **İzin kartları**:
  - *Evet* yalnızca bu işleme izin verir.
  - *Evet, bir daha sorma* Claude Code'un önerdiği kalıcı kuralı ekler.
  - *Hayır* işlemi reddeder ve talimatınızı bekler.
  - *Değişikliği göster* önerilen düzenlemeyi diff görünümünde açar.
- **Plan modu**: Claude planı hazırladığında plan bir kartta gösterilir. Onaylarken düzenlemelerin otomatik kabul edilip edilmeyeceğini seçersiniz.
- **Sorular**: Claude seçenekli bir soru sorduğunda seçenekler kartta listelenir.
- **Üst çubuk**:
  - *Yeni oturum* (`/clear`) yeni bir konuşma başlatır.
  - İzin modu menüsü: her işlemde sor, düzenlemeleri kabul et, plan, otomatik, izinleri atla.
  - Model menüsü: liste Claude Code'dan alınır.
- **Kullanım şeridi**: 5 saatlik oturum limiti ile haftalık ve modele özel limitlerin doluluk oranını ve sıfırlanma zamanını gösterir.
  - Sohbetten bağımsızdır: panel açıldığında, 5 dakikada bir, her yanıttan sonra ve ⟳ ile yenilenir.
  - Sohbet oturumu yokken kısa ömürlü ayrı bir `claude.exe` yalnızca kullanım bilgisini sorar. Model çağrısı yapılmadığı için kullanım harcamaz.
  - Pro/Max/Team/Enterprise aboneliğinde kullanım plan limitlerinden düşer; bu yüzden tur sonunda dolar tutarı gösterilmez.
    Yalnızca API anahtarıyla (token başına ücretli) kullanımda "API maliyeti" gösterilir.
- Yanıtlardaki dosya bağlantılarına ve dosya yolu içeren kod parçalarına tıklayınca dosya Visual Studio'da ilgili satırda açılır.
  Kod bloklarındaki *Kopyala* ve *Ekle* butonları kodu panoya kopyalar ya da imlecin bulunduğu yere ekler.
- `/` ile başlayan diğer komutlar (`/compact`, `/cost`, kendi komutlarınız) doğrudan Claude Code'a iletilir.

## Ayarlar

**Araçlar > Seçenekler > Claude Code > Genel**

| Ayar | Açıklama |
|---|---|
| claude.exe yolu | Boşsa otomatik aranır |
| Model | `opus`, `sonnet`, `haiku`, `fable` veya tam model kimliği; boşsa varsayılan |
| İzin modu | Yeni oturumların başlangıç modu |
| Ek argümanlar | `claude.exe`'ye eklenecek argümanlar (ör. `--add-dir D:\ortak`) |
| Göndermeden önce dosyaları kaydet | Claude'un güncel içeriği görmesi için (varsayılan: açık) |
| Aktif dosyayı bağlam olarak ekle | Onay kutusunun varsayılan durumu |
| Visual Studio sistem istemi | Claude'a VS içinde çalıştığını ve tıklanabilir dosya bağlantıları kullanmasını söyler |

Çalışma klasörü, açık çözümün (veya açılan klasörün) dizinidir. Çözüm değişirse bir sonraki mesajda yeni oturum başlar.

## Gizlilik ve kurumsal kullanım

**Önce temel gerçek:** Claude'un modeli bulutta çalışır. Claude'un yanıt üretmek için gördüğü her şey seçili model sağlayıcısına
gönderilir: mesajlar, eklenen kod, araçların okuduğu dosyalar ve komut çıktıları. Hiçbir eklenti ayarı bunu sıfırlayamaz; verinin
hangi kurallarla gittiği aşağıdaki üç katmanla belirlenir.

### 1. Sözleşme ve sağlayıcı (verinin nereye gittiği)

- Ticari hesaplarda (Claude Team/Enterprise, API, Bedrock, Vertex, Foundry) Anthropic kod ve istemleri **model eğitiminde
  kullanmaz**. Standart saklama süresi 30 gündür.
- **Sıfır veri saklama (ZDR)**, Claude for Enterprise'da Anthropic hesap ekibi aracılığıyla açılabilir; Team planında yoktur.
- Kodun şirketin kendi bulut sözleşmesi altında kalması isteniyorsa **Araçlar > Seçenekler > Claude Code > Model sağlayıcısı**:
  | Sağlayıcı | Ortam değişkenleri (örnek) |
  |---|---|
  | AmazonBedrock | `AWS_REGION=eu-central-1; AWS_PROFILE=sirket` |
  | GoogleVertexAI | `CLOUD_ML_REGION=europe-west1; ANTHROPIC_VERTEX_PROJECT_ID=proje-id` |
  | MicrosoftFoundry | `ANTHROPIC_FOUNDRY_RESOURCE=kaynak-adi` |
  | CorporateGateway | `ANTHROPIC_BASE_URL=https://llm-gateway.sirket.local; ANTHROPIC_AUTH_TOKEN=…` |

  Bu durumda veri saklama ve işleme o bulut sağlayıcısının sözleşmesine tabidir. Ağ geçidi kullanılırsa abonelik girişi yerine
  ağ geçidinin kimlik bilgisi kullanılır.

### 2. Bu eklentinin gizlilik modu (varsayılan: açık)

Panelin üstündeki **Gizli mod** rozeti etkin korumaları gösterir; tıklayınca ayrıntılı özet açılır.

- Telemetri, hata raporu, geri bildirim (`/feedback`, `/bug`) ve otomatik güncelleme trafiği kapalıdır
  (`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, `DISABLE_TELEMETRY` …). Remote Control kapalıdır.
- İçeriği dışarı yayınlayan ya da paylaşan araçlar kaldırılır: Artifact, paylaşım, uzak tetikleyici, bildirim, geri bildirim.
- Web araçları (WebFetch/WebSearch) ve kullanıcı/proje MCP sunucuları varsayılan olarak devre dışıdır.
- **Korunan dosyalar** (`.env`, `*.pfx`, `*.pem`, `**/secrets/**` …) Claude tarafından okunamaz, düzenlenemez ve bağlam olarak
  eklenmez. Desenler ayarlardan değiştirilebilir.
- **Gizli bilgi taraması:** Gönderilecek mesaj ve eklenen kod API anahtarı, parola, bağlantı dizesi, token, özel anahtar gibi
  değerler için taranır. Bulunursa değer gösterilmeden uyarılır ve «GİZLİ» ile maskelemesi önerilir.
- "İzinleri atla" ve "Otomatik" modları kaldırılır; her kabuk komutu onay bekler.
- **Ağ komutları engellenir** (Bash ve PowerShell): curl, wget, Invoke-WebRequest, Invoke-RestMethod, Start-BitsTransfer, scp…
  Onay verilse bile çalışmazlar. Ağa veri gönderebilecek diğer komutlar (`git push`, http adresleri, betik içinden HTTP…)
  izin kartında uyarıyla işaretlenir ve "bir daha sorma" seçeneği sunulmaz.
- Not: Telemetri kapatıldığında Claude Code PowerShell aracını da kapatıyor; eklenti `CLAUDE_CODE_USE_POWERSHELL_TOOL=1` ile
  aracı açık tutar (merkezi politikada da aynısı yapılmalıdır, örnek dosyada var).
- Her turun sonunda içeriği modele gönderilen dosyaların sayısı gösterilir (üzerine gelince liste).
- **Yerel gönderim günlüğü:** Mesajlar, eklenen dosyalar, araçların eriştiği dosya/komutlar ve izin kararları yalnızca bu
  bilgisayarda `%LOCALAPPDATA%\ClaudeCodeVS\audit\YYYY-AA-GG.jsonl` dosyalarına yazılır.

Bu katman kullanıcı tarafından kapatılabilir. Zorunlu kılmak için 3. katman gerekir.

### 3. Merkezi politika (BT ekibi için, zorunlu)

Claude Code, `C:\Program Files\ClaudeCode\managed-settings.json` dosyasındaki (veya `HKLM\SOFTWARE\Policies\ClaudeCode` kayıt
defteri anahtarındaki) ayarları her çalıştığı yerde uygular; kullanıcı ayarları ve bu eklenti onları geçersiz kılamaz.
Başlangıç için [docs/managed-settings.ornek.json](docs/managed-settings.ornek.json) dosyasını kullanabilirsiniz.

- Ek olarak değerlendirin: `forceLoginMethod` / `forceLoginOrgUUID` (yalnızca şirket organizasyonu hesabıyla giriş),
  `allowManagedPermissionRulesOnly` (yalnızca merkezi izin kuralları), `allowManagedMcpServersOnly`,
  `permissions.disableAutoMode`, `allowedProviders`. Ayrıntılar: https://code.claude.com/docs/en/managed-settings
- Konuşma kayıtları her makinede `%USERPROFILE%\.claude\projects` altında düz metin olarak tutulur; süre `cleanupPeriodDays`
  ile kısaltılabilir.
- İzin kuralları savunma katmanıdır, kesin sınır değildir (ör. bir komut başka bir programın içinden ağa çıkabilir). Kesin sınır
  için ağ tarafında yalnızca model sağlayıcısının adresine izin veren bir proxy/güvenlik duvarı kuralı kullanın.

## Nasıl çalışır

Eklenti, resmi Agent SDK'ların yaptığı gibi `claude.exe`'yi uzun ömürlü bir alt süreç olarak başlatır ve stdin/stdout
üzerinden satır satır JSON konuşur:

```
claude --output-format stream-json --verbose --input-format stream-json
       --include-partial-messages --permission-prompt-tool stdio [--model …] [--permission-mode …] [--resume …]
```

- Kullanıcı mesajları `{"type":"user",…}` satırları olarak gönderilir. Yanıtlar `system`, `stream_event`, `assistant`, `user` (araç
  sonuçları) ve `result` mesajları olarak akar.
- İzinler kontrol protokolüyle gelir. CLI `can_use_tool` isteği gönderir, eklenti `allow`/`deny` yanıtı döner. Durdurma, mod ve
  model değişikliği ile kullanım sorgusu da aynı protokolü kullanır: `interrupt`, `set_permission_mode`, `set_model`, `get_usage`.
- `claude.exe` ve başlattığı kabuklar bir Windows Job Object'e bağlıdır. Oturum kapanınca ya da Visual Studio çökerse hepsi sonlanır.
- Oturum kimliği saklanır. Süreç beklenmedik şekilde kapanırsa bir sonraki mesajda `--resume` ile konuşma sürdürülür.

Bu protokol CLI'ın SDK'lar için sunduğu arayüzdür ve Claude Code 2.1.311 ile test edilmiştir. Claude Code'un ileriki sürümlerinde
değişirse eklentinin güncellenmesi gerekebilir.

## Sorun giderme

- **Kurulumdan sonra menüde "Claude Code" görünmüyor.** Visual Studio'yu kapatın ve yapılandırma önbelleğini yenileyin.
  Deneysel örnek için sona `/rootsuffix Exp` ekleyin.
  ```powershell
  & "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" /updateconfiguration
  ```
- **F5 sırasında `VSSDK1031: Extension ... could not be found`.** Aynı önbellek sorunudur; yukarıdaki komutu `/rootsuffix Exp` ile çalıştırıp tekrar deneyin.
- **Deneysel örnek çözüm açılırken donuyor.** Deneysel örnekte kurulu başka eklentileri kontrol edin. Temiz bir ortamda denemek
  için farklı bir kök sonek kullanabilirsiniz:
  `VSIXInstaller.exe /rootSuffix:ClaudeTest ClaudeCodeVS.vsix` ve ardından `devenv.exe /rootsuffix ClaudeTest`.
- **"Claude Code'da oturum açılmamış olabilir" uyarısı.** Bir terminalde `claude` çalıştırıp `/login` ile giriş yapın, sonra panelde
  *Yeni oturum*'a basın.

## Proje yapısı

```
src/ClaudeCodeVS/
  Cli/          claude.exe bulma, süreç ve protokol (ClaudeProcess), oturum (ClaudeSession), kullanım, Job Object
  Ide/          aktif dosya/seçim, çözüm klasörü, dosya açma, diff önizleme
  UI/           Markdown görüntüleyici, araç çağrısı / izin / soru kartları, kullanım şeridi, tema
  ToolWindows/  araç penceresi ve sohbet kontrolü
  Commands/     menü komutları
  Options/      Araçlar > Seçenekler sayfası
```

`Cli/` klasörü Visual Studio'ya bağımlı değildir; ayrı bir .NET Framework uygulamasından da kullanılabilir.
