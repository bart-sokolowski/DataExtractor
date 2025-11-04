using DataExtractor.Models;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;

namespace DataExtractor.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }

        [BindProperty]
        public string? Urls { get; set; }

        [TempData]
        public string? ExtractedItemsJson { get; set; }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrWhiteSpace(Urls))
            {
                ModelState.AddModelError(string.Empty, "Please provide at least one URL.");
                return Page();
            }

            var urlList = Urls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();

            var items = new List<ExtractedListing>();

            using var http = new HttpClient();
            foreach (var url in urlList)
            {
                try
                {
                    var html = await http.GetStringAsync(url);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var item = new ExtractedListing { Url = url };

                    // Title: try og:title then title tag
                    var ogTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", null);
                    item.Title = !string.IsNullOrWhiteSpace(ogTitle)
                        ? CleanText(ogTitle)
                        : CleanText(doc.DocumentNode.SelectSingleNode("//title")?.InnerText) ?? string.Empty;

                    // Description
                    var desc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", null)
                               ?? doc.DocumentNode.SelectSingleNode("//div[@data-testid='property-description']//p")?.InnerText
                               ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'hotel_desc')]")?.InnerText;
                    item.Description = CleanText(desc);

                    // Images
                    item.Images.AddRange(ExtractImages(doc));

                    // Rating
                    item.Rating = ExtractRating(doc);

                    // Price
                    item.Price = ExtractPrice(doc);

                    // Location/address
                    var loc = doc.DocumentNode.SelectSingleNode("//span[@data-testid='address']")?.InnerText
                              ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'hp_address_subtitle')]")?.InnerText
                              ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'address')]")?.InnerText;
                    item.Location = CleanText(loc);

                    items.Add(item);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract {Url}", url);
                    items.Add(new ExtractedListing
                    {
                        Url = url,
                        Title = "(Failed to fetch)",
                        Description = ex.Message
                    });
                }
            }

            ExtractedItemsJson = JsonSerializer.Serialize(items);
            return RedirectToPage("Results");
        }

        private string? ExtractRating(HtmlDocument doc)
        {
            var ratingScore = CleanText(doc.DocumentNode
                .SelectSingleNode("//div[@data-testid='review-score-right-component']//div[1]")?.InnerText);
            var ratingLabel = CleanText(doc.DocumentNode
                .SelectSingleNode("//div[@data-testid='review-score-right-component']//div[2]")?.InnerText);

            if (!string.IsNullOrWhiteSpace(ratingScore))
            {
                if (!string.IsNullOrWhiteSpace(ratingLabel))
                    return $"{ratingScore} ({ratingLabel})";
                return ratingScore;
            }

            var ariaScoreNode = doc.DocumentNode.SelectNodes("//span[@aria-label]")?.FirstOrDefault(node =>
            {
                var value = node.GetAttributeValue("aria-label", string.Empty);
                var normalized = value.ToLowerInvariant();
                return normalized.Contains("score") || normalized.Contains("rating");
            });
            var ariaScore = CleanText(ariaScoreNode?.GetAttributeValue("aria-label", null));
            if (!string.IsNullOrWhiteSpace(ariaScore))
                return ariaScore;

            var starNode = doc.DocumentNode.SelectSingleNode("//div[@data-testid='rating-stars']")
                           ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'bk-icon-stars')]");
            var starText = CleanText(starNode?.InnerText);
            if (!string.IsNullOrWhiteSpace(starText))
            {
                var normalizedStarText = NormalizeStarRating(starText);
                if (!string.IsNullOrWhiteSpace(normalizedStarText))
                    return normalizedStarText;
            }

            var ratingNodes = new[]
            {
                doc.DocumentNode.SelectSingleNode("//div[@data-testid='review-score']"),
                doc.DocumentNode.SelectSingleNode("//div[contains(@class,'review-score')]")
            };

            foreach (var node in ratingNodes)
            {
                var text = CleanText(node?.InnerText);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            var metaRating = CleanText(doc.DocumentNode.SelectSingleNode("//meta[@itemprop='ratingValue']")?.GetAttributeValue("content", null));
            if (!string.IsNullOrWhiteSpace(metaRating))
                return metaRating;

            return null;
        }

        private string? ExtractPrice(HtmlDocument doc)
        {
            var priceSelectors = new[]
            {
                "//span[@data-testid='price-and-discounted-price']",
                "//div[@data-testid='price-and-discounted-price']",
                "//span[@data-testid='price-for-x-nights']",
                "//span[@data-testid='price-per-night']",
                "//span[@data-testid='total-price-value']",
                "//div[@data-testid='price-summary']//span",
                "//span[@data-testid='price-summary']",
                "//div[contains(@class,'prco-valign-center-helper')]",
                "//span[contains(@class,'prco-inline-block-maker-helper')]",
                "//span[contains(@class,'bui-price-display__value')]",
                "//div[contains(@class,'bui-price-display__value')]",
                "//span[contains(@class,'price')]",
                "//div[contains(@class,'price')]"
            };

            foreach (var selector in priceSelectors)
            {
                var node = doc.DocumentNode.SelectSingleNode(selector);
                var value = CleanText(node?.InnerText);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;

                var ariaValue = CleanText(node?.GetAttributeValue("aria-label", null));
                if (!string.IsNullOrWhiteSpace(ariaValue))
                    return ariaValue;
            }

            var metaPrice = CleanText(doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:amount']")?.GetAttributeValue("content", null));
            if (!string.IsNullOrWhiteSpace(metaPrice))
            {
                var currency = CleanText(doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:currency']")?.GetAttributeValue("content", null));
                if (!string.IsNullOrWhiteSpace(currency))
                    return $"{metaPrice} {currency}";
                return metaPrice;
            }

            return null;
        }

        private List<string> ExtractImages(HtmlDocument doc)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddImage(string? candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                var cleaned = candidate.Trim();
                if (seen.Add(cleaned))
                    results.Add(cleaned);
            }

            var ogImageNodes = doc.DocumentNode.SelectNodes("//meta[@property='og:image']");
            if (ogImageNodes != null)
            {
                foreach (var node in ogImageNodes)
                {
                    AddImage(node.GetAttributeValue("content", null));
                    if (results.Count >= 5)
                        return results;
                }
            }

            var gallerySelectors = new[]
            {
                "//img[@data-testid='image']",
                "//img[@data-testid='hero-image']",
                "//div[@data-testid='image-gallery']//img",
                "//img[contains(@class,'hotel_image')]",
                "//img[contains(@src,'/images/hotel/max')]"
            };

            foreach (var selector in gallerySelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes == null)
                    continue;

                foreach (var node in nodes)
                {
                    var src = node.GetAttributeValue("src", null) ?? node.GetAttributeValue("data-src", null);
                    AddImage(src);
                    if (results.Count >= 5)
                        return results;
                }
            }

            if (results.Count == 0)
            {
                var fallback = doc.DocumentNode.SelectSingleNode("//img[1]");
                var src = fallback?.GetAttributeValue("src", null) ?? fallback?.GetAttributeValue("data-src", null);
                AddImage(src);
            }

            return results;
        }

        private string? CleanText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var value = input;
            for (var i = 0; i < 3; i++)
            {
                var decoded = HtmlEntity.DeEntitize(value);
                if (decoded == value)
                    break;
                value = decoded;
            }

            value = value.Replace('\u00a0', ' ');
            value = Regex.Replace(value, "\\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value;
        }

        private string? NormalizeStarRating(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var starCount = text.Count(c => c == '★');
            if (starCount > 0)
            {
                var remainder = text.Replace("★", string.Empty).Trim();
                var summary = $"{new string('★', starCount)} ({starCount}-star property)";
                return string.IsNullOrWhiteSpace(remainder) ? summary : $"{summary} – {remainder}";
            }

            return text;
        }
    }
}
