# Publishing Valafind to the Microsoft Store and the Mac App Store

Every release that CI publishes on GitHub (not prereleases) is also sent to both stores by
[`.github/workflows/store.yml`](../../.github/workflows/store.yml), which `ci.yml` calls after its `release` job. You can
also run **Actions › Store release › Run workflow** with a released tag such as `v1.0.42` to send (or resend) that
version.

Each store's job is skipped until its repository variables are set (`MSSTORE_PRODUCT_ID` for the Microsoft Store,
`APPLE_TEAM_ID` for the Mac App Store), so merging this setup changes nothing until you finish the steps below.

| | Microsoft Store | Mac App Store |
|---|---|---|
| Account | [Partner Center](https://partner.microsoft.com/dashboard/registration), free for individuals and companies | [Apple Developer Program](https://developer.apple.com/programs/enroll/), 99 USD a year |
| What CI builds | `.msixupload` with an x64 and an arm64 MSIX (unsigned; the Store signs it) | `.pkg` of a sandboxed, Apple-silicon-only `.app`, signed for the App Store |
| How CI submits | `msstore publish` ([Microsoft Store Developer CLI](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview)) once a Microsoft Entra application is set up (step 2); until then, CI builds the package and you upload it | `fastlane deliver`, with an App Store Connect API key |
| After submitting | Certification (hours to a few days), then the Store publishes it | App Review (usually a day or two), then Apple releases it |

Store copies never install or update themselves from GitHub (`StoreInstall` detects an MSIX package identity on Windows
and the App Sandbox on macOS); the store updates them.

## How store copies differ

**Mac App Store.** The App Sandbox lets an app read only its own container and the folders the user picks in an open
panel, and only until it quits. So the store copy:

- indexes **only the folders listed in Settings** (no home-folder default), and opens Settings at start while there are
  none; the launcher shows "No folders" until then;
- makes a read-only, security-scoped bookmark for every folder the user adds
  ([`FolderAccess`](../../src/Valafind.App/Platform/FolderAccess.cs),
  [`MacSandbox.cs`](../../src/Valafind.App/Platform/MacOS/MacSandbox.cs)), keeps it in `settings.json`
  (`RootBookmarks`), and regains every folder from it at start. A folder that was deleted is dropped from the list; one
  that was moved is followed;
- starts at login as a login item (`SMAppService.mainAppService`, System Settings › General › Login Items), off until
  the user turns it on, as App Review guideline 2.4.5 requires.

Things to try on a Mac with a TestFlight build before the first submission, since they cannot be tested elsewhere:
adding a folder, quitting and restarting (the folder must still be indexed), the login item, and the helpers Valafind
starts as child processes, which inherit the sandbox: `mdfind` (recent documents), `qlmanage` (previews), `open`
(opening and revealing files) and `ffmpeg` (only a fallback; AVFoundation decodes media).

**Microsoft Store.** The package runs with full trust, so indexing works as in the GitHub build. Windows starts Valafind
at login through the package's startup task (with `--background`); the settings window says to change that in
**Settings › Apps › Startup** instead of offering its own switch, because a package's writes to the `Run` key stay
inside the package.

## Microsoft Store

### 1. Reserve the app

1. Sign in to [Partner Center](https://partner.microsoft.com/dashboard) and open **Apps and games › New product ›
   MSIX or PWA app**. Reserve the name **Valafind**.
2. Open the app's **Product management › Product identity** and note:
   - **Store ID** (12 characters, starts with `9`): repository variable `MSSTORE_PRODUCT_ID`
   - **Package/Identity/Name**: `MSSTORE_IDENTITY_NAME`
   - **Package/Identity/Publisher** (`CN=…`): `MSSTORE_PUBLISHER`
   - **Package/Properties/PublisherDisplayName**: `MSSTORE_PUBLISHER_DISPLAY_NAME`

### 2. Give CI access to Partner Center

The Store API signs in as a Microsoft Entra application that has the **Manager** role in your Partner Center account. The
**Microsoft Entra applications** tab where you add one only appears once a Microsoft Entra tenant is associated with the
account. Microsoft documents this for company accounts and says nothing about individual ones, but individual accounts
are doing it: publishers with free individual accounts have associated a tenant and submit from CI with `msstore`
(November 2026). The links below are the pages the `msstore` CLI itself opens.

1. **Associate a tenant.** Open [Account settings › Tenants](https://partner.microsoft.com/dashboard/account/TenantSetup)
   and either choose **Associate Microsoft Entra ID with your Partner Center account** and sign in with a work account of
   an existing tenant (not your personal Microsoft account), or choose **Create Microsoft Entra ID** to make a new, free
   tenant. Microsoft's guides:
   [associate an existing tenant](https://learn.microsoft.com/windows/apps/publish/partner-center/associate-existing-azure-ad-tenant-with-partner-center-account),
   [create a new tenant](https://learn.microsoft.com/windows/apps/publish/partner-center/create-new-azure-ad-tenant).
   Don't invite your personal Microsoft account into the tenant as a guest.
2. **Open the applications tab.** Sign out of Partner Center and back in, then open
   [Account settings › User management › Microsoft Entra applications](https://partner.microsoft.com/dashboard/account/v3/usermanagement#apps).
   It needs a **Manager** who is also global administrator of the tenant. If only the **Users** tab shows, add the
   tenant's admin (for example `you@yourtenant.onmicrosoft.com`) under
   [Users](https://partner.microsoft.com/dashboard/account/v3/usermanagement) with the **Manager** role, sign in to
   Partner Center as that account and look again; the change can take a while to show.
3. **Create the application from Partner Center.** Choose **Add Microsoft Entra application › Create Microsoft Entra
   application**, give it a name and any reply URL, and select **Manager (Windows)** under *Roles applicable to developer
   programs* ([Microsoft's guide](https://learn.microsoft.com/windows/apps/publish/partner-center/manage-azure-ad-applications-in-partner-center)).
   Apps registered in the Entra admin center and added afterwards have failed with "Identity cannot be authorized"
   ([msstore-cli #131](https://github.com/microsoft/msstore-cli/issues/131)); creating it here avoids that.
4. **Make a key.** Select the application, then **Add new key**. The **Tenant ID**, **Client ID** and **Key** go into the
   secrets `MSSTORE_TENANT_ID`, `MSSTORE_CLIENT_ID` and `MSSTORE_CLIENT_SECRET`. Keys expire after one or two years;
   renew the secret when they do.
5. **Copy the Seller ID** from [Account settings › Legal info](https://partner.microsoft.com/dashboard/account/v3/organization/legalinfo#mpn)
   into `MSSTORE_SELLER_ID`.

If the tenant can't be associated, or the tab still doesn't appear, open a support request from
[Partner Center](https://partner.microsoft.com/dashboard) (**?** › Help and support): Microsoft has published no rule
either way ([one such report](https://github.com/MicrosoftDocs/windows-dev-docs/issues/6153)). Until it works, leave the
four `MSSTORE_…` secrets unset: every release then builds the package, keeps it for 90 days as the run's
**microsoft-store** artifact and writes upload instructions in the run's summary, for uploading by hand (step 3).

### 3. Submissions

The Store API can only update an app whose first submission exists, so make that one in Partner Center:

1. Set the repository variable `STORE_DRAFT_ONLY` to `true`, then run **Actions › Store release** with the latest release
   tag (or let the next release run it).
   - With the API secrets, it uploads the package and leaves the submission as a draft.
   - Without them, download the run's **microsoft-store** artifact, unzip it, and in Partner Center start the submission
     and drop the `.msixupload` under **Packages**.
2. Fill in **Pricing and availability**, **Properties** (category, privacy policy URL), **Age ratings** and the **Store
   listing** (see [The Store listing](#the-store-listing): one import fills every language). Under **Submission options**, explain the
   `runFullTrust` restricted capability: "Valafind is a desktop app (packaged Win32/.NET) and needs full trust to index the user's files in the background, register its global hotkey and open files in their apps."
3. **Submit to the Store**.
4. When it is published, delete `STORE_DRAFT_ONLY` (or set it to `false`). With the API secrets, every later release is
   submitted and published without you; without them, upload the releases you want from their **microsoft-store**
   artifacts the same way (you don't have to upload every one).

### The Store listing

The listing (texts, five screenshots and the logos, in all 21 UI languages) is generated, so it never has to be typed in
Partner Center:

1. Run **Actions › Store listing** (or `packaging/store/listing/make-listing.sh` locally; it needs the .NET SDK,
   Python 3 and the Noto fonts) and download the **store-listing** artifact.
2. In Partner Center, open the submission's **Store listings**, add the languages you want under **Manage Store listing
   languages**, then **Import listings › Upload folder** and pick `valafind-store-listing-all` (every language) or a
   `valafind-store-listing-<language>` folder (one language). Unzip it first; upload the folder itself.
3. If the import rejects a language column, Partner Center names that language differently: **Export listings**, then
   run `make-listing.sh <out> -- --columns-from <the exported .csv>` and import again.

What goes in:

- Texts: [`listing/text/<language>.json`](listing/text/en.json), English being the source. Change `en.json`, then update
  the translations the same way (their keys, order and limits are checked when the folders are built).
- Screenshots (1920×1080): rendered from the real Windows UI by
  [`StoreListingScreenshotTests`](../../tests/Valafind.App.Tests/Headless/StoreListingScreenshotTests.cs), with the
  UI, headline and search query in each language. The test also draws the 9:16 poster, 1:1 box art and 300×300 tile.
- Fields: [`listing/fields.csv`](listing/fields.csv) is Partner Center's list of listing fields (from an export);
  [`listing/build_listing.py`](listing/build_listing.py) fills it.

## Mac App Store

### 1. Identifiers, certificates and the provisioning profile

All in [Certificates, Identifiers & Profiles](https://developer.apple.com/account/resources):

1. **Identifiers › +** › App IDs › App, platform macOS, explicit bundle ID **`app.valafind.Valafind`** (the one in
   [`macos/Info.plist`](macos/Info.plist)). No capabilities are needed.
2. **Certificates › +**: create an **Apple Distribution** certificate and a **Mac Installer Distribution** certificate
   (each from a certificate signing request made with Keychain Access › Certificate Assistant). Download both and open
   them so they land in your login keychain with their private keys.
3. In Keychain Access select both certificates (under **My Certificates**), **File › Export Items…** as one `.p12` with a
   password. Then:
   ```bash
   base64 -i Certificates.p12 | pbcopy   # secret MAS_CERTIFICATES_P12; the password goes into MAS_CERTIFICATES_PASSWORD
   ```
4. **Profiles › +** › Distribution › **Mac App Store Connect**, App ID `app.valafind.Valafind`, the Apple Distribution certificate.
   Download it, then:
   ```bash
   base64 -i Valafind_Mac_App_Store.provisionprofile | pbcopy   # secret MAS_PROVISIONING_PROFILE
   ```
   Profiles expire after a year; renew this secret when it does.
5. Your **Team ID** (Membership details, 10 characters) goes into the variable `APPLE_TEAM_ID`.

The two certificates and the API key below are per team, so both Shapeshifter and Valafind use the same ones; the
provisioning profile is per app.

### 2. The app record and the API key

1. In [App Store Connect](https://appstoreconnect.apple.com/apps) choose **+ › New App**, platform macOS, bundle ID
   `app.valafind.Valafind`, any SKU.
2. **Users and Access › Integrations › App Store Connect API › Team Keys › +**, access **App Manager**. Note the **Key ID**
   (variable `APP_STORE_CONNECT_KEY_ID`) and the **Issuer ID** above the table (variable `APP_STORE_CONNECT_ISSUER_ID`),
   and download the `.p8` file (possible only once). Its contents, from `-----BEGIN PRIVATE KEY-----` to the end line,
   are the secret `APP_STORE_CONNECT_PRIVATE_KEY`.

### 3. The first submission

1. With `STORE_DRAFT_ONLY=true`, run **Actions › Store release**: it uploads the build and creates the version without
   submitting it.
2. In App Store Connect fill in everything the first version needs: description, keywords, support URL, privacy policy
   URL, **App Privacy** answers, **Pricing and Availability**, the age rating and Mac screenshots (1280×800, 1440×900,
   2560×1600 or 2880×1800). In **App Review Information**, explain what the app does and which permissions it asks for
   (it indexes only the folders the user picks, entirely on the Mac, and makes no network connections).
3. Select the uploaded build and **Add for Review**. After the first approval, set `STORE_DRAFT_ONLY` to `false`: every
   later release is uploaded, given release notes that link to its GitHub release, submitted, and released when approved.

## Charging for the app

The store versions are paid; the GitHub releases stay free. Both stores pay out only once their business details are in:

- **Microsoft Store:** in Partner Center, fill in **Account settings › Payout and tax**
  (a payout account and the tax interview) before submitting a paid app. Under the app's **Pricing and availability**,
  choose a **base price**. Keep to a base price rather than separate prices per market: when you publish an update,
  `msstore publish` keeps the app's base price, but it stops with an error if the price is set per market
  ([msstore CLI › Paid apps and pricing](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/commands#paid-apps-and-pricing)).
  Microsoft's GitHub Actions guide still says CLI updates are for free products only, while the newer CLI reference
  describes paid updates; if a paid update is refused, upload that release's **microsoft-store** artifact by hand.
- **Mac App Store:** in [App Store Connect › Business](https://appstoreconnect.apple.com/business), accept the **Paid
  Apps Agreement** and add your bank account and tax forms. Paid apps can't be submitted until the agreement is active.
  Then choose a price under the app's **Pricing and Availability**.

## All secrets and variables

Set them under **Settings › Secrets and variables › Actions** of this repository, or with the GitHub CLI:

```bash
gh secret set MSSTORE_CLIENT_SECRET --repo ffMathy/valafind
```

```bash
gh variable set MSSTORE_PRODUCT_ID --repo ffMathy/valafind --body 9NXXXXXXXXXX
```

| Name | Kind | Value |
|---|---|---|
| `MSSTORE_PRODUCT_ID` | variable | Store ID (`9N…`); turns the Microsoft Store job on |
| `MSSTORE_IDENTITY_NAME` | variable | Package/Identity/Name |
| `MSSTORE_PUBLISHER` | variable | Package/Identity/Publisher (`CN=…`) |
| `MSSTORE_PUBLISHER_DISPLAY_NAME` | variable | Package/Properties/PublisherDisplayName |
| `MSSTORE_TENANT_ID` | secret | Entra tenant ID (leave the four `MSSTORE_…` secrets unset to upload by hand) |
| `MSSTORE_SELLER_ID` | secret | Partner Center seller ID |
| `MSSTORE_CLIENT_ID` | secret | Entra application (client) ID |
| `MSSTORE_CLIENT_SECRET` | secret | Entra application key |
| `APPLE_TEAM_ID` | variable | Apple team ID; turns the Mac App Store job on |
| `APP_STORE_CONNECT_KEY_ID` | variable | API key ID |
| `APP_STORE_CONNECT_ISSUER_ID` | variable | API issuer ID |
| `APP_STORE_CONNECT_PRIVATE_KEY` | secret | Contents of `AuthKey_<key id>.p8` |
| `MAS_CERTIFICATES_P12` | secret | Base64 of the `.p12` with both certificates and their keys |
| `MAS_CERTIFICATES_PASSWORD` | secret | That `.p12`'s password |
| `MAS_PROVISIONING_PROFILE` | secret | Base64 of the Mac App Store Connect profile |
| `STORE_DRAFT_ONLY` | variable | `true` to upload without submitting (first submissions) |

## Files

- [`windows/AppxManifest.xml`](windows/AppxManifest.xml): the MSIX manifest. Besides the app it declares a startup task
  (start at login; packaged apps cannot use the `Run` registry key), which users switch in **Settings › Apps › Startup**.
- [`windows/make-msix.ps1`](windows/make-msix.ps1): packs `dotnet publish` output into the `.msixupload`.
- [`listing/`](listing): the Store listing generator (see [The Store listing](#the-store-listing)).
- [`macos/Info.plist`](macos/Info.plist), [`macos/Entitlements.plist`](macos/Entitlements.plist): the App Store bundle's
  property list and its sandbox entitlements.
- [`macos/make-pkg.sh`](macos/make-pkg.sh): builds, signs and packages the `.app`.
- `windows/Images`, `macos/AppIcon.png`: generated by [`make-images.py`](make-images.py) from the app icon (Valafind's icon exists only at 256 px, so the 1024 px Mac icon is scaled up: draw a 1024 px master and rerun the script before submitting).
