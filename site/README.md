# The website (SITE-010)
One page per language (`/he/`, `/de/`, …) built from `template.html` and `i18n/<lang>.json`; the start page sends each
visitor to the language of their region (I18N-020). Before publishing, set `site.json`: the product name, the site
address (`baseUrl`), the partner portal address (`portalUrl`) and the sales e-mail.

    node tools/site/build.mjs site/dist        # build
    node tools/site/check.mjs site/dist        # check every language, computer and phone

`site/dist` is a plain static folder: it can be served by IIS, any web host, or a static host (Netlify, Cloudflare Pages).
CI builds it on every push (artifact "website").
