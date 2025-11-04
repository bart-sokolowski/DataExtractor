using DataExtractor.Models;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using System;
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
        public List<string> UrlEntries { get; set; } = new();

        [TempData]
        public string? ExtractedItemsJson { get; set; }

        public void OnGet()
        {
            EnsureUrlInputs();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            UrlEntries = UrlEntries?
                .Select(u => u?.Trim() ?? string.Empty)
                .ToList() ?? new List<string>();

            var urlList = UrlEntries
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();

            if (!urlList.Any())
            {
                ModelState.AddModelError(string.Empty, "Please provide at least one URL.");
                EnsureUrlInputs();
                return Page();
            }

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

                    // Occupancy / number of people
                    item.Occupancy = ExtractOccupancy(doc, url);

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

        private void EnsureUrlInputs()
        {
            if (UrlEntries == null)
            {
                UrlEntries = new List<string>();
            }

            if (UrlEntries.Count == 0)
            {
                UrlEntries.Add(string.Empty);
            }
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
                "//*[@id='group_recommendation']//td[contains(@class,'totalPrice-container')]//span[contains(@class,'prco-valign-middle-helper')]",
                "//*[@id='group_recommendation']//td[contains(@class,'totalPrice-container')]//div[contains(@class,'bui-price-display__value')]//span",
                "//*[@id='group_recommendation']//td[contains(@class,'totalPrice-container')]//div[contains(@class,'bui-price-display__value')]",
                "//td[contains(@class,'totalPrice-container')]//span[contains(@class,'prco-valign-middle-helper')]",
                "//td[contains(@class,'totalPrice-container')]//div[contains(@class,'bui-price-display__value')]//span",
                "//td[contains(@class,'totalPrice-container')]//div[contains(@class,'bui-price-display__value')]",
                "//span[@data-testid='price-and-discounted-price']",
                "//div[@data-testid='price-and-discounted-price']",
                "//span[@data-testid='price-for-x-nights']",
                "//span[@data-testid='price-per-night']",
                "//span[@data-testid='total-price-value']",
                "//div[@data-testid='price-summary']//span",
                "//span[@data-testid='price-summary']",
                "//div[@data-testid='price-summary']",
                "//div[@data-testid='availability-cta']//*[contains(@class,'price')]",
                "//span[@data-testid='property-price']",
                "//div[@data-testid='property-price']",
                "//div[contains(@class,'hp__hotel-prices')]/span",
                "//div[contains(@class,'hotel_price_block_wrapper')]//strong",
                "//div[contains(@class,'bui-price-display__value')]//span",
                "//div[contains(@class,'prco-valign-center-helper')]",
                "//span[contains(@class,'prco-inline-block-maker-helper')]",
                "//span[contains(@class,'bui-price-display__value')]",
                "//div[contains(@class,'bui-price-display__value')]",
                "//span[contains(@class,'price')]",
                "//div[contains(@class,'price')]",
                "//td[contains(@class,'totalPrice')]//div[contains(@class,'bui-price-display__value')]//span",
                "//td[contains(@class,'totalPrice')]//div[contains(@class,'prco-valign-middle-helper')]",
                "//td[contains(@class,'totalPrice')]//span[contains(@class,'prco-inline-block-maker-helper')]"
            };

            foreach (var selector in priceSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes == null)
                    continue;

                foreach (var node in nodes)
                {
                    var value = NormalizePriceText(node?.InnerText);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;

                    var ariaValue = NormalizePriceText(node?.GetAttributeValue("aria-label", null));
                    if (!string.IsNullOrWhiteSpace(ariaValue))
                        return ariaValue;

                    var dataPrice = NormalizePriceText(node?.GetAttributeValue("data-price", null));
                    if (!string.IsNullOrWhiteSpace(dataPrice))
                        return dataPrice;
                }
            }

            var metaPrice = CleanText(doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:amount']")?.GetAttributeValue("content", null));
            if (!string.IsNullOrWhiteSpace(metaPrice))
            {
                var currency = CleanText(doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:currency']")?.GetAttributeValue("content", null));
                if (!string.IsNullOrWhiteSpace(currency))
                    return $"{metaPrice} {currency}";
                return metaPrice;
            }

            var scriptPrice = ExtractFromScripts(doc, "priceDisplayable", "displayPrice", "priceDisplayValue", "display_price", "priceString");
            var normalizedScriptPrice = NormalizePriceText(scriptPrice);
            if (!string.IsNullOrWhiteSpace(normalizedScriptPrice))
                return normalizedScriptPrice;

            var scriptNodes = doc.DocumentNode.SelectNodes("//script");
            if (scriptNodes != null)
            {
                var amountPattern = new Regex("\"price\"\\s*:\\s*\\{[^}]*\"amount\"\\s*:\\s*(?<amount>[0-9]+(?:\\.[0-9]+)?)\\s*,[^}]*\"currency\"\\s*:\\s*\"(?<currency>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (var script in scriptNodes)
                {
                    var text = script.InnerText;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var match = amountPattern.Match(text);
                    if (match.Success)
                    {
                        var amount = match.Groups["amount"].Value;
                        var currency = match.Groups["currency"].Value;
                        var combined = NormalizePriceText($"{amount} {currency}");
                        if (!string.IsNullOrWhiteSpace(combined))
                            return combined;
                    }
                }
            }

            return null;
        }

        private string? ExtractOccupancy(HtmlDocument doc, string sourceUrl)
        {
            var occupancySelectors = new[]
            {
                "//div[@data-testid='occupancy-config']",
                "//span[@data-testid='occupancy-config']",
                "//div[@data-testid='max-people-message']",
                "//div[contains(@class,'occupancy-message')]",
                "//span[contains(@class,'occupancy-message')]",
                "//div[contains(@class,'c-occupancy-icons__text')]",
                "//span[contains(@class,'c-occupancy-icons__text')]",
                "//div[contains(@class,'room-config__occupancy')]",
                "//span[contains(@class,'room-config__occupancy')]",
                "//td[contains(@class,'totalPrice')]//div[contains(@class,'bui-price-display__label')]",
                "//td[contains(@class,'totalPrice-container')]//div[contains(@class,'bui-price-display__label')]"
            };

            foreach (var selector in occupancySelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes == null)
                    continue;

                foreach (var node in nodes)
                {
                    var value = NormalizeOccupancyText(node?.InnerText);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            var keywordSelectors = new[]
            {
                "//span[contains(text(),'Sleeps')]",
                "//div[contains(text(),'Sleeps')]",
                "//li[contains(text(),'Sleeps')]",
                "//span[contains(text(),'guests')]",
                "//div[contains(text(),'guests')]",
                "//li[contains(text(),'guests')]"
            };

            foreach (var selector in keywordSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes == null)
                    continue;

                foreach (var node in nodes)
                {
                    var value = NormalizeOccupancyText(node?.InnerText);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            var scriptOccupancy = ExtractFromScripts(doc, "occupancyText", "occupancyDisplayValue", "occupancySummary", "maxOccupancy");
            if (!string.IsNullOrWhiteSpace(scriptOccupancy))
            {
                if (int.TryParse(scriptOccupancy, out var count) && count > 0)
                    return count == 1 ? "Sleeps 1" : $"Sleeps {count}";

                var normalizedScript = NormalizeOccupancyText(scriptOccupancy);
                if (!string.IsNullOrWhiteSpace(normalizedScript))
                    return normalizedScript;
            }

            var scriptNodes = doc.DocumentNode.SelectNodes("//script");
            if (scriptNodes != null)
            {
                var occupancyPattern = new Regex("\"adults\"\\s*:\\s*(?<adults>\\d+)(?:[^\\d]+\"children\"\\s*:\\s*(?<children>\\d+))?", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (var script in scriptNodes)
                {
                    var text = script.InnerText;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var match = occupancyPattern.Match(text);
                    if (match.Success)
                    {
                        var adults = int.Parse(match.Groups["adults"].Value);
                        var childrenGroup = match.Groups["children"];
                        var children = 0;
                        if (childrenGroup.Success && int.TryParse(childrenGroup.Value, out var parsedChildren))
                            children = parsedChildren;

                        var total = adults + children;
                        if (total > 0)
                            return total == 1 ? "Sleeps 1" : $"Sleeps {total}";
                    }
                }
            }

            var occupancyFromUrl = ExtractOccupancyFromUrl(sourceUrl);
            if (!string.IsNullOrWhiteSpace(occupancyFromUrl))
                return occupancyFromUrl;

            return null;
        }

        private string? NormalizePriceText(string? value)
        {
            var cleaned = CleanText(value);
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            if (!Regex.IsMatch(cleaned, "\\d"))
                return null;

            if (Regex.IsMatch(cleaned, "we price match", RegexOptions.IgnoreCase))
                return null;

            cleaned = Regex.Replace(cleaned, "^(price|total|cost)[:\\s-]*", string.Empty, RegexOptions.IgnoreCase).Trim();
            cleaned = Regex.Replace(cleaned, "includes taxes and charges", string.Empty, RegexOptions.IgnoreCase).Trim();

            if (!Regex.IsMatch(cleaned, "\\d"))
                return null;

            return cleaned;
        }

        private string? NormalizeOccupancyText(string? value)
        {
            var cleaned = CleanText(value);
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            if (!Regex.IsMatch(cleaned, "\\d"))
                return null;

            if (Regex.IsMatch(cleaned, "facilities", RegexOptions.IgnoreCase))
                return null;

            var normalized = cleaned.ToLowerInvariant();
            if (!(normalized.Contains("night") || normalized.Contains("guest") || normalized.Contains("adult") || normalized.Contains("person") || normalized.Contains("people") || normalized.Contains("sleep")))
                return null;

            return cleaned;
        }

        private string? ExtractOccupancyFromUrl(string sourceUrl)
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
                return null;

            var query = QueryHelpers.ParseQuery(uri.Query);

            int ParseCount(string key)
            {
                if (query.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed > 0)
                    return parsed;
                return 0;
            }

            var adults = ParseCount("group_adults");
            var children = ParseCount("group_children");
            var rooms = ParseCount("no_rooms");

            int nights = 0;
            if (query.TryGetValue("checkin", out var checkinValue) && query.TryGetValue("checkout", out var checkoutValue))
            {
                if (DateTime.TryParse(checkinValue, out var checkin) && DateTime.TryParse(checkoutValue, out var checkout) && checkout > checkin)
                {
                    nights = (int)(checkout - checkin).TotalDays;
                }
            }

            var parts = new List<string>();
            if (nights > 0)
                parts.Add(nights == 1 ? "1 night" : $"{nights} nights");

            if (adults > 0)
                parts.Add(adults == 1 ? "1 adult" : $"{adults} adults");

            if (children > 0)
                parts.Add(children == 1 ? "1 child" : $"{children} children");

            if (rooms > 0)
                parts.Add(rooms == 1 ? "1 room" : $"{rooms} rooms");

            if (parts.Count == 0)
                return null;

            return string.Join(", ", parts);
        }

        private string? ExtractFromScripts(HtmlDocument doc, params string[] keys)
        {
            if (keys == null || keys.Length == 0)
                return null;

            var scriptNodes = doc.DocumentNode.SelectNodes("//script");
            if (scriptNodes == null)
                return null;

            foreach (var script in scriptNodes)
            {
                var content = script.InnerText;
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                foreach (var key in keys)
                {
                    var pattern = $"\\\"{Regex.Escape(key)}\\\"\\s*:\\s*\\\"(?<value>.*?)\\\"";
                    var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (match.Success)
                    {
                        var raw = Regex.Unescape(match.Groups["value"].Value);
                        var cleaned = CleanText(raw);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            return cleaned;
                    }
                }
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
