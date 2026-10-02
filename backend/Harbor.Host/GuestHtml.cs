using System.Net;
using System.Text;

namespace Harbor.Host;

static class GuestHtml
{
    public static string Consent(string clientName, IQueryCollection query)
    {
        var fields = new StringBuilder();
        foreach (var pair in query)
        {
            foreach (var value in pair.Value)
            {
                fields.Append("<input type=\"hidden\" name=\"")
                    .Append(WebUtility.HtmlEncode(pair.Key))
                    .Append("\" value=\"")
                    .Append(WebUtility.HtmlEncode(value))
                    .Append("\">");
            }
        }

        var body = $"""
            <p class="brand"><span class="mark" aria-hidden="true">H</span><span>Harbor</span></p>
            <h1>Allow access</h1>
            <p class="name">{WebUtility.HtmlEncode(clientName)}</p>
            <p>This client can act as you.</p>
            <form method="post" action="/connect/authorize">
            {fields}
            <button class="button" type="submit" name="decision" value="accept">Accept</button>
            <button class="button quiet" type="submit" name="decision" value="deny">Deny</button>
            </form>
            """;
        return Document.Replace("__HARBOR_BODY__", body, StringComparison.Ordinal);
    }

    public static string Notice(string message, string href, string label)
    {
        var body = $"""
            <p class="brand"><span class="mark" aria-hidden="true">H</span><span>Harbor</span></p>
            <p>{WebUtility.HtmlEncode(message)}</p>
            <a class="button" href="{WebUtility.HtmlEncode(href)}">{WebUtility.HtmlEncode(label)}</a>
            """;
        return Document.Replace("__HARBOR_BODY__", body, StringComparison.Ordinal);
    }

    // Colors match frontend/src/styles/theme.css and the guest card in styles.css.
    private const string Document = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="light dark">
        <title>Harbor</title>
        <style>
        :root {
          color-scheme: light dark;
          --tint: var(--indigo);
          --gray: oklch(0.5 0 0);
          --indigo: oklch(1 0.25049 284.23);
          --tint-100: oklch(from var(--tint) var(--lightness-100) var(--chroma-100) h);
          --tint-200: oklch(from var(--tint) var(--lightness-200) var(--chroma-200) h);
          --tint-300: oklch(from var(--tint) var(--lightness-300) var(--chroma-300) h);
          --tint-400: oklch(from var(--tint) var(--lightness-400) var(--chroma-400) h);
          --tint-1000: oklch(from var(--tint) var(--lightness-1000) var(--chroma-1000) h);
          --tint-1400: oklch(from var(--tint) var(--lightness-1400) var(--chroma-1400) h);
          --gray-100: oklch(from var(--gray) var(--lightness-100) c h);
          --gray-300: oklch(from var(--gray) var(--lightness-300) c h);
          --gray-400: oklch(from var(--gray) var(--lightness-400) c h);
          --gray-500: oklch(from var(--gray) var(--lightness-500) c h);
          --gray-600: oklch(from var(--gray) var(--lightness-600) c h);
          --gray-1000: oklch(from var(--gray) var(--lightness-1000) c h);
          --gray-1200: oklch(from var(--gray) var(--lightness-1200) c h);
          --gray-1300: oklch(from var(--gray) var(--lightness-1300) c h);
          --background-color: #f8f8f8;
          --gray-50: #ffffff;
          --lightness-100: 98.1187%;
          --lightness-200: 95.2045%;
          --lightness-300: 91.1434%;
          --lightness-400: 85.1751%;
          --lightness-500: 79.1773%;
          --lightness-600: 72.3297%;
          --lightness-1000: 51.9076%;
          --lightness-1200: 41.0821%;
          --lightness-1300: 35.3616%;
          --lightness-1400: 29.6725%;
          --chroma-100: calc(l * c * 0.5);
          --chroma-200: calc(l * c * 0.6);
          --chroma-300: calc(l * c * 0.7);
          --chroma-400: calc(l * c * 0.8);
          --chroma-1000: c;
          --chroma-1400: c;
          --overlay-background: var(--gray-50);
          --focus-ring-color: var(--tint-1000);
          --text-color: var(--gray-1200);
          --text-color-hover: var(--gray-1300);
          --text-color-placeholder: var(--gray-1000);
          --border-color: var(--gray-400);
          --border-color-hover: var(--gray-500);
          --highlight-background: oklch(from var(--tint) 55% c h);
          --highlight-foreground: white;
          --font-size: 0.875rem;
          --font-size-sm: 0.75rem;
          --font-size-lg: 1rem;
          --radius: 8px;
          --radius-lg: 10px;
          --spacing: 0.25rem;
          --spacing-1: var(--spacing);
          --spacing-2: calc(2 * var(--spacing));
          --spacing-3: calc(3 * var(--spacing));
          --spacing-4: calc(4 * var(--spacing));
          --spacing-8: calc(8 * var(--spacing));
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --background-color: #1b1b1b;
            --gray-50: oklch(22% 0 0);
            --lightness-100: 29.6725%;
            --lightness-200: 35.3616%;
            --lightness-300: 41.0821%;
            --lightness-400: 46.9058%;
            --lightness-500: 51.9076%;
            --lightness-600: 57.9699%;
            --lightness-1000: 67.0121%;
            --lightness-1200: 79.1773%;
            --lightness-1300: 85.1751%;
            --lightness-1400: 91.1434%;
            --overlay-background: var(--gray-100);
          }
        }
        @media (min-resolution: 200dpi) and (max-width: 720px) {
          :root {
            --spacing: calc(0.25rem * 1.25);
            --font-size: 1.0625rem;
            --font-size-sm: 0.9375rem;
            --font-size-lg: 1.25rem;
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0;
          min-height: 100dvh;
          background: var(--background-color);
          color: var(--text-color);
          font: var(--font-size) system-ui;
        }
        .guest {
          min-height: 100dvh;
          display: grid;
          place-items: safe center;
          padding: max(var(--spacing-4), env(safe-area-inset-top)) var(--spacing-4) max(var(--spacing-4), env(safe-area-inset-bottom));
        }
        .card {
          width: min(100%, 24rem);
          display: flex;
          flex-direction: column;
          gap: var(--spacing-3);
          background: var(--overlay-background);
          border: 0.5px solid var(--border-color);
          border-radius: var(--radius-lg);
          padding: var(--spacing-4);
        }
        .brand {
          display: flex;
          align-items: center;
          gap: var(--spacing-2);
          margin: 0;
          padding-bottom: var(--spacing-3);
          border-bottom: 0.5px solid var(--border-color);
          font-weight: 600;
        }
        .mark {
          display: grid;
          place-items: center;
          width: var(--spacing-8);
          height: var(--spacing-8);
          border-radius: var(--radius);
          background: var(--highlight-background);
          color: var(--highlight-foreground);
          font-size: var(--font-size-sm);
          font-weight: 700;
        }
        h1 {
          margin: 0;
          font-size: var(--font-size-lg);
          font-weight: 600;
        }
        p { margin: 0; }
        .name { font-weight: 600; }
        form {
          display: flex;
          flex-direction: column;
          gap: var(--spacing-2);
        }
        .button {
          --button-color: var(--tint);
          --button-background: oklch(from var(--button-color) var(--lightness-100) var(--chroma-100) h);
          --button-gradient: oklch(from var(--button-color) var(--lightness-200) var(--chroma-200) h);
          --button-border: oklch(from var(--button-color) var(--lightness-300) var(--chroma-300) h);
          --button-highlight: rgb(255 255 255 / 0.8);
          --button-shadow: oklch(from var(--button-color) var(--lightness-400) var(--chroma-400) h);
          --button-border-size: 1px;
          --button-text: oklch(from var(--button-color) var(--lightness-1400) var(--chroma-1400) h);
          --button-gradient-size: 8px;
          appearance: none;
          width: 100%;
          height: var(--spacing-8);
          margin: 0;
          padding: 0 var(--spacing-3);
          border: none;
          border-radius: var(--radius);
          background: var(--button-background);
          color: var(--button-text);
          box-shadow:
            inset 0 -1px 0 var(--button-shadow),
            inset 0 0 0 var(--button-border-size) var(--button-border),
            inset 0px calc(var(--button-border-size) + 1px) 0px var(--button-highlight),
            inset 0px calc(-1 * var(--button-gradient-size)) var(--button-gradient-size) -2px var(--button-gradient);
          font: var(--font-size) system-ui;
          font-weight: 500;
          text-decoration: none;
          display: inline-flex;
          align-items: center;
          justify-content: center;
          cursor: pointer;
        }
        @media (prefers-color-scheme: dark) {
          .button:not(.quiet) {
            --button-shadow: oklch(from var(--button-color) var(--lightness-200) var(--chroma-200) h);
            --button-highlight: rgb(255 255 255 / 0.15);
            box-shadow:
              inset 0 var(--button-border-size) 0 var(--button-highlight),
              inset 0 calc(-1 * var(--button-border-size)) 0 var(--button-shadow),
              inset 0 0 0 var(--button-border-size) var(--button-border),
              inset 0 var(--button-gradient-size) var(--button-gradient-size) -2px var(--button-gradient);
          }
        }
        .button:hover:not(.quiet) {
          --button-background: oklch(from var(--button-color) var(--lightness-400) var(--chroma-400) h);
          --button-text: white;
          background: var(--button-background);
          color: var(--button-text);
        }
        .button:active { scale: 0.95; }
        .button:focus-visible {
          outline: 2px solid var(--focus-ring-color);
          outline-offset: 2px;
        }
        .button.quiet {
          --button-background: transparent;
          --button-text: var(--text-color);
          background: transparent;
          color: var(--text-color);
          box-shadow: inset 0 0 0 1px var(--border-color);
        }
        .button.quiet:hover {
          background: var(--gray-300);
          color: var(--text-color-hover);
          box-shadow: inset 0 0 0 1px var(--border-color-hover);
        }
        </style>
        </head>
        <body>
        <main class="guest">
        <div class="card">
        __HARBOR_BODY__
        </div>
        </main>
        </body>
        </html>
        """;
}
