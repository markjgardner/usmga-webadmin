# USMGA static site

This folder contains the public USMGA website rebuilt as an Eleventy static site for Azure Static Web Apps.

## Structure

- `src/` - Eleventy pages, layouts, navigation data, and assets.
- `src/_includes/base.njk` - shared page shell and navigation.
- `src/assets/css/styles.css` - custom responsive theme.
- `_site/` - generated static output (ignored by git).
- `staticwebapp.config.json` - Azure Static Web Apps routing and headers.

## Commands

```bash
npm install
npm run build
npm run serve
```

The build output is `_site/`.
