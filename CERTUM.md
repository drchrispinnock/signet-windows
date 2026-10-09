# Getting a Certum code-signing certificate

Windows Smart App Control blocks unsigned programs it doesn't recognise, including fresh builds of
`Signet.exe`. A certificate from a certificate authority Windows trusts fixes that; a self-signed
certificate does not. Certum's Open Source Code Signing certificate is the cheapest one that can
sign builds on this PC.

## Before you buy

- **Public project.** Certum issues this certificate only for a project that is publicly available
  and clearly shows you as its author. If they can't identify the project from public information,
  no certificate is issued. The Mac repository (`github.com/drchrispinnock/signet`) would do if it's
  public. This Windows repository has no remote yet; publishing it is a separate decision.
- **Who it's for.** Individuals and non-commercial open-source use only.
- **Validity.** Since 27 February 2026 a code-signing certificate lasts at most 459 days, so
  multi-year purchases need reissuing during the term.

## Steps

1. **Buy.** At [shop.certum.eu](https://shop.certum.eu/open-source-code-signing-on-simplysign.html),
   order **"Open Source Code Signing in the Cloud"** (from about €49). Choose the cloud
   (SimplySign) version, not the bundle with a physical card.
2. **Start activation.** In your Certum account, open **My account → Data security products** and
   start activating the certificate. Enter your details and the project URL.
3. **Verify your identity.** Either use the online check (photo ID and a short face check on your
   camera), or upload scans of your ID (both sides). Send documents through the same account.
4. **Wait for issuance.** When the certificate is issued you get three emails: "Welcome to Certum",
   "Regaining access" (with a link) and "Secret" (with a code).
5. **Set up the apps.** Install **SimplySign** on your phone and **SimplySign Desktop** on this PC.
   Open the "Regaining access" link, enter the code from the "Secret" email, and scan the QR code
   it shows with the phone app.
6. **Sign in on the PC.** Log in to SimplySign Desktop with a one-time code from the phone app;
   you do this each time you start it. Then right-click its tray icon and choose
   **Managing Certificates → List of Certificates** so the certificate appears in Windows.

## After that

With SimplySign Desktop signed in, the build can sign `Signet.exe` and Signet's own DLLs with
Microsoft's `signtool` (already in the project's build tools) and Certum's timestamp server. That
step is still to be added to the build. It will do nothing when no certificate is present, so
builds keep working without it.

If a screen doesn't match these steps, Certum's own activation guide (below) is the reference.

## Sources

- [Certum Shop – Open Source Code Signing in the Cloud](https://shop.certum.eu/open-source-code-signing-on-simplysign.html)
- [Certum: How to activate the CS certificate on SimplySign](https://support.certum.eu/en/how-to-activate-code-signing-simplysign/)
- [Certum: Open Source Code Signing in the cloud – certificate activation (PDF)](https://www.files.certum.eu/documents/manual_en/CS-Open_Source_Code_Signing_in_the_cloud_Certificate_activation.pdf)
- [Certum: Code Signing – required documents](https://support.certum.eu/en/code-signing-required-documents/)
- [Certum: How to activate the SimplySign application](https://support.certum.eu/en/how-to-activate-access-to-simply-sign-application/)
- [Open Source Code Signing Certificates in 2026 (My-SSL)](https://my-ssl.com/learn/open-source-code-signing-certificate)
- [LeaderSSL: Installing and activating Certum Code Signing in the Cloud](https://www.leaderssl.com/articles/617-how-to-install-and-activate-certum-code-signing-in-the-cloud-standard-and-ev)
