using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace DocuMind.Web.Infrastructure;

/// <summary>
/// Puts the antiforgery token into every <c>&lt;form method="post"&gt;</c>.
/// The built-in form tag helper only does this for forms routed with <c>asp-*</c> attributes, not for
/// forms with a literal <c>action="..."</c>; without the token, the global
/// <c>AutoValidateAntiforgeryToken</c> filter rejects the post. Runs after the built-in helper and
/// skips forms that already have a token.
/// </summary>
[HtmlTargetElement("form", Attributes = "method")]
public sealed class AntiforgeryFormTagHelper(IAntiforgery antiforgery) : TagHelper
{
    public override int Order => 1000;

    [HtmlAttributeNotBound, ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var method = output.Attributes["method"]?.Value?.ToString();
        if (!string.Equals(method, "post", StringComparison.OrdinalIgnoreCase)) return;

        var tokens = antiforgery.GetAndStoreTokens(ViewContext.HttpContext);
        if (output.PostContent.GetContent().Contains(tokens.FormFieldName, StringComparison.Ordinal)) return;

        output.PostContent.AppendHtml($"<input name=\"{tokens.FormFieldName}\" type=\"hidden\" value=\"{tokens.RequestToken}\" />");
    }
}
