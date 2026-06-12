using DataExtractor.Models;
using DataExtractor.Services;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;

namespace DataExtractor.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        private readonly BrowserPageFetcher _pageFetcher;
        private readonly ListingStore _listingStore;
        private const string BookingBaseUrl = "https://www.booking.com";

        public IndexModel(ILogger<IndexModel> logger, BrowserPageFetcher pageFetcher, ListingStore listingStore)
        {
            _logger = logger;
            _pageFetcher = pageFetcher;
            _listingStore = listingStore;
        }

        [BindProperty]
        public List<string> UrlEntries { get; set; } = new();

        public IReadOnlyList<ExtractedListing> Listings { get; private set; } = Array.Empty<ExtractedListing>();

        public void OnGet()
        {
            EnsureUrlInputs();
            Listings = _listingStore.GetAll();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            UrlEntries = UrlEntries?
                .Select(u => u?.Trim() ?? string.Empty)
                .ToList() ?? new List<string>();

            var urlList = UrlEntries
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!urlList.Any())
            {
                ModelState.AddModelError(string.Empty, "Please provide at least one URL.");
                EnsureUrlInputs();
                Listings = _listingStore.GetAll();
                return Page();
            }

            var items = await Task.WhenAll(urlList.Select(ExtractListingAsync));
            _listingStore.AddOrUpdate(items);

            return RedirectToPage("Index", null, "listings");
        }

        public IActionResult OnPostRemove(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
                _listingStore.Remove(id);

            return RedirectToPage("Index", null, "listings");
        }

        public async Task<IActionResult> OnGetExportPdfAsync()
        {
            if (!_listingStore.GetAll().Any())
                return RedirectToPage("Index");

            var printUrl = $"{Request.Scheme}://{Request.Host}/Print";
            var pdfBytes = await _pageFetcher.RenderPdfAsync(printUrl);

            return File(pdfBytes, "application/pdf", $"listings-{DateTime.Now:yyyy-MM-dd}.pdf");
        }

        private async Task<ExtractedListing> ExtractListingAsync(string url)
        {
            if (!IsSupportedListingUrl(url))
            {
                return new ExtractedListing
                {
                    Url = url,
                    Title = "(Unsupported URL)",
                    Description = "Only Booking.com listing URLs are supported."
                };
            }

            try
            {
                var html = await _pageFetcher.FetchHtmlAsync(url);
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

                PopulateFromJsonLd(doc, item);

                var mapData = ExtractMapData(doc, item.Location);
                item.MapImageUrl ??= mapData.mapImageUrl;
                item.MapLink ??= mapData.mapLink;

                if (string.IsNullOrWhiteSpace(item.Title) && string.IsNullOrWhiteSpace(item.Description) && string.IsNullOrWhiteSpace(item.Price) && !item.Images.Any())
                {
                    item.Title = "(Details unavailable)";
                    item.Description = BrowserPageFetcher.LooksLikeBotChallenge(html)
                        ? "Booking.com blocked the request with a bot challenge. Please try again in a moment."
                        : "We couldn't read the listing details from Booking.com.";
                }

                return item;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract {Url}", url);
                return new ExtractedListing
                {
                    Url = url,
                    Title = "(Failed to fetch)",
                    Description = "We couldn't fetch this listing. Please check the URL and try again."
                };
            }
        }

        private static bool IsSupportedListingUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            return uri.Host.Equals("booking.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.EndsWith(".booking.com", StringComparison.OrdinalIgnoreCase);
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

        private static string BuildMapsLink(string query) =>
            $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(query)}";

        private (string? mapImageUrl, string? mapLink) ExtractMapData(HtmlDocument doc, string? locationText)
        {
            string? mapImage = null;
            string? mapLink = null;
            string? coordinateQuery = null;

            var scriptMapLink = ExtractFromScripts(doc, "googleMapsUrl", "google_maps_url", "googleMapsLink", "google_map_link");
            if (!string.IsNullOrWhiteSpace(scriptMapLink))
                mapLink = NormalizeUrl(scriptMapLink);

            var atlasNode = doc.DocumentNode.SelectSingleNode("//*[@data-atlas-latlng]");
            var atlasCoords = atlasNode?.GetAttributeValue("data-atlas-latlng", null);
            if (!string.IsNullOrWhiteSpace(atlasCoords) && atlasCoords.Contains(','))
                coordinateQuery = CleanText(atlasCoords);

            var atlasExplicitLink = NormalizeUrl(atlasNode?.GetAttributeValue("data-google-maps-url", null))
                                     ?? NormalizeUrl(atlasNode?.GetAttributeValue("data-maps-url", null));
            if (!string.IsNullOrWhiteSpace(atlasExplicitLink))
                mapLink ??= atlasExplicitLink;

            var anchorNode = doc.DocumentNode.SelectSingleNode("//a[@data-atlas-latlng or @data-google-maps-url or contains(@class,'show_map') or contains(@class,'js-map-link') or contains(@class,'map_link')]");
            if (anchorNode != null)
            {
                var anchorLink = NormalizeUrl(anchorNode.GetAttributeValue("data-google-maps-url", null))
                                 ?? NormalizeUrl(anchorNode.GetAttributeValue("href", null));
                if (!string.IsNullOrWhiteSpace(anchorLink))
                    mapLink ??= anchorLink;

                if (string.IsNullOrWhiteSpace(coordinateQuery))
                {
                    var anchorCoords = anchorNode.GetAttributeValue("data-atlas-latlng", null);
                    if (!string.IsNullOrWhiteSpace(anchorCoords) && anchorCoords.Contains(','))
                        coordinateQuery = CleanText(anchorCoords);
                }
            }

            var latMeta = CleanText(doc.DocumentNode.SelectSingleNode("//meta[@property='booking_com:location:latitude']")?.GetAttributeValue("content", null));
            var lonMeta = CleanText(doc.DocumentNode.SelectSingleNode("//meta[@property='booking_com:location:longitude']")?.GetAttributeValue("content", null));
            if (!string.IsNullOrWhiteSpace(latMeta) && !string.IsNullOrWhiteSpace(lonMeta))
            {
                coordinateQuery ??= $"{latMeta},{lonMeta}";
            }

            var mapImageNode = doc.DocumentNode.SelectSingleNode("//img[contains(@class,'map_static') or contains(@class,'map-image') or contains(@class,'map_static_image') or contains(@src,'static_map') or contains(@src,'maps.gstatic.com')]");
            if (mapImageNode != null)
            {
                mapImage = NormalizeUrl(mapImageNode.GetAttributeValue("src", null), BookingBaseUrl)
                           ?? NormalizeUrl(mapImageNode.GetAttributeValue("data-src", null), BookingBaseUrl)
                           ?? NormalizeUrl(mapImageNode.GetAttributeValue("data-lazy-src", null), BookingBaseUrl);
            }

            if (string.IsNullOrWhiteSpace(mapImage))
            {
                var mapContainer = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'map_static') or contains(@class,'map-container') or contains(@class,'map_static_image') or contains(@data-static-map-url,'http') or contains(@data-atlas-lazy-image,'http')]");
                if (mapContainer != null)
                {
                    mapImage = NormalizeUrl(mapContainer.GetAttributeValue("data-static-map-url", null), BookingBaseUrl)
                               ?? NormalizeUrl(mapContainer.GetAttributeValue("data-atlas-lazy-image", null), BookingBaseUrl)
                               ?? NormalizeUrl(mapContainer.GetAttributeValue("data-lazy-url", null), BookingBaseUrl);

                    if (string.IsNullOrWhiteSpace(mapImage))
                    {
                        var style = mapContainer.GetAttributeValue("style", null);
                        if (!string.IsNullOrWhiteSpace(style))
                        {
                            var match = Regex.Match(style, @"url\((['""]?)(?<url>[^'"")]+)['""]?\)");
                            if (match.Success)
                                mapImage = NormalizeUrl(match.Groups["url"].Value, BookingBaseUrl);
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(mapImage))
            {
                var scriptMapImage = ExtractFromScripts(doc, "staticMapUrl", "static_map_url", "mapStaticImageUrl", "map_image_url", "staticMapImageUrl");
                if (!string.IsNullOrWhiteSpace(scriptMapImage))
                    mapImage = NormalizeUrl(scriptMapImage, BookingBaseUrl);
            }

            if (string.IsNullOrWhiteSpace(mapLink))
            {
                var scriptLinkFallback = ExtractFromScripts(doc, "googleMapsUrl", "google_maps_url", "googleMapsLink", "google_map_link", "mapsUrl");
                if (!string.IsNullOrWhiteSpace(scriptLinkFallback))
                    mapLink = NormalizeUrl(scriptLinkFallback);
            }

            if (string.IsNullOrWhiteSpace(mapLink) || !mapLink.Contains("google.com/maps", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(coordinateQuery))
                {
                    mapLink = BuildMapsLink(coordinateQuery);
                }
                else if (!string.IsNullOrWhiteSpace(locationText))
                {
                    mapLink = BuildMapsLink(locationText);
                }
            }

            return (mapImage, mapLink);
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
            static IEnumerable<HtmlNode> GetPriceCandidates(HtmlNode cell)
            {
                var explicitValueNodes = cell.SelectNodes(".//div[contains(@class,'bui-price-display__value')]//span" +
                                                          "|.//div[contains(@class,'bui-price-display__value')]" +
                                                          "|.//span[contains(@class,'prco-valign-middle-helper')]" +
                                                          "|.//span[contains(@class,'prco-inline-block-maker-helper')]");

                if (explicitValueNodes != null)
                    foreach (var node in explicitValueNodes)
                        yield return node;

                yield return cell;
            }

            var totalPriceCells = doc.DocumentNode.SelectNodes("//td[contains(concat(' ', normalize-space(@class), ' '), ' totalPrice ')]");
            if (totalPriceCells != null)
            {
                foreach (var cell in totalPriceCells)
                {
                    foreach (var node in GetPriceCandidates(cell))
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

            cleaned = Regex.Replace(cleaned, "^(price|total|cost)[^\\d£€$]*", string.Empty, RegexOptions.IgnoreCase).Trim();
            cleaned = Regex.Replace(cleaned, "includes taxes and charges", string.Empty, RegexOptions.IgnoreCase).Trim();

            if (!Regex.IsMatch(cleaned, "\\d"))
                return null;

            var currencyMatch = Regex.Match(
                cleaned,
                @"((?:£|€|\$|¥|₩|₹|₽|₺|₪|฿|₫|₱)\s*[\d,.]+)|((?:AUD|CAD|CHF|DKK|EUR|GBP|NOK|NZD|PLN|RON|SEK|USD|AED|SAR|CNY|JPY|INR|KRW|SGD|HKD)\s*[\d,.]+)",
                RegexOptions.IgnoreCase);
            if (currencyMatch.Success)
            {
                var result = CleanText(currencyMatch.Value);
                if (!string.IsNullOrWhiteSpace(result))
                    return result;
            }

            if (Regex.IsMatch(cleaned, "(night|adult|guest|person|people|room)", RegexOptions.IgnoreCase))
                return null;

            var numericMatch = Regex.Match(cleaned, "\\d[\\d,.\\s]*");
            if (numericMatch.Success)
                return CleanText(numericMatch.Value);

            return null;
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

        private void PopulateFromJsonLd(HtmlDocument doc, ExtractedListing item)
        {
            var scriptNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (scriptNodes == null)
                return;

            foreach (var script in scriptNodes)
            {
                var content = script.InnerText;
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                try
                {
                    using var jsonDoc = JsonDocument.Parse(content);
                    ProcessJsonLdElement(jsonDoc.RootElement, item);
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to parse JSON-LD content for {Url}", item.Url);
                }
            }
        }

        private void ProcessJsonLdElement(JsonElement element, ExtractedListing item)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    ProcessJsonLdObject(element, item);
                    break;
                case JsonValueKind.Array:
                    foreach (var child in element.EnumerateArray())
                        ProcessJsonLdElement(child, item);
                    break;
            }
        }

        private void ProcessJsonLdObject(JsonElement element, ExtractedListing item)
        {
            var type = TryGetString(element, "@type");
            var normalizedType = type?.ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(normalizedType))
            {
                if (normalizedType.Contains("offer"))
                {
                    if (string.IsNullOrWhiteSpace(item.Price))
                    {
                        var offerPrice = ExtractPriceFromOffers(element);
                        if (!string.IsNullOrWhiteSpace(offerPrice))
                            item.Price = offerPrice;
                    }
                }
                else if (normalizedType.Contains("hotel") || normalizedType.Contains("lodging") || normalizedType.Contains("accommodation") || normalizedType.Contains("place") || normalizedType.Contains("residence") || normalizedType.Contains("product"))
                {
                    if (string.IsNullOrWhiteSpace(item.Title))
                    {
                        var name = CleanText(TryGetString(element, "name"));
                        if (!string.IsNullOrWhiteSpace(name))
                            item.Title = name;
                    }

                    if (string.IsNullOrWhiteSpace(item.Description))
                    {
                        var description = CleanText(TryGetString(element, "description"));
                        if (!string.IsNullOrWhiteSpace(description))
                            item.Description = description;
                    }

                    if (!item.Images.Any() && element.TryGetProperty("image", out var imageElement))
                        ExtractImagesFromJson(imageElement, item);

                    if (string.IsNullOrWhiteSpace(item.Location) && element.TryGetProperty("address", out var addressElement))
                    {
                        var address = ExtractAddressFromJson(addressElement);
                        if (!string.IsNullOrWhiteSpace(address))
                            item.Location = address;
                    }

                    if (string.IsNullOrWhiteSpace(item.Price) && element.TryGetProperty("offers", out var offersElement))
                    {
                        var offerPrice = ExtractPriceFromOffers(offersElement);
                        if (!string.IsNullOrWhiteSpace(offerPrice))
                            item.Price = offerPrice;
                    }

                    if (string.IsNullOrWhiteSpace(item.Price) && element.TryGetProperty("priceRange", out var priceRangeElement))
                    {
                        var normalized = NormalizePriceText(TryGetString(priceRangeElement));
                        if (!string.IsNullOrWhiteSpace(normalized))
                            item.Price = normalized;
                    }

                    if (string.IsNullOrWhiteSpace(item.Rating) && element.TryGetProperty("aggregateRating", out var ratingElement))
                    {
                        var rating = CleanText(TryGetString(ratingElement, "ratingValue"));
                        if (!string.IsNullOrWhiteSpace(rating))
                            item.Rating = rating;
                    }

                    if (string.IsNullOrWhiteSpace(item.MapLink) && element.TryGetProperty("hasMap", out var mapElement))
                    {
                        var link = NormalizeUrl(TryGetString(mapElement));
                        if (!string.IsNullOrWhiteSpace(link))
                            item.MapLink = link;
                    }
                }
            }

            if (element.TryGetProperty("geo", out var geoElement))
                ApplyGeoToListing(geoElement, item);

            if (string.IsNullOrWhiteSpace(item.Price) && element.TryGetProperty("offers", out var nestedOffersElement))
            {
                var offerPrice = ExtractPriceFromOffers(nestedOffersElement);
                if (!string.IsNullOrWhiteSpace(offerPrice))
                    item.Price = offerPrice;
            }

            foreach (var property in element.EnumerateObject())
            {
                ProcessJsonLdElement(property.Value, item);
            }
        }

        private void ExtractImagesFromJson(JsonElement element, ExtractedListing item)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    AddImageToListing(item, element.GetString());
                    break;
                case JsonValueKind.Array:
                    foreach (var child in element.EnumerateArray())
                        ExtractImagesFromJson(child, item);
                    break;
                case JsonValueKind.Object:
                    if (element.TryGetProperty("url", out var urlElement))
                    {
                        AddImageToListing(item, TryGetString(urlElement));
                    }
                    else
                    {
                        foreach (var property in element.EnumerateObject())
                        {
                            if (property.NameEquals("@type"))
                                continue;

                            ExtractImagesFromJson(property.Value, item);
                        }
                    }
                    break;
            }
        }

        private string? ExtractAddressFromJson(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return CleanText(element.GetString());

            if (element.ValueKind != JsonValueKind.Object)
                return null;

            var parts = new List<string>();

            void AddPart(string? value)
            {
                var cleaned = CleanText(value);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    parts.Add(cleaned);
            }

            AddPart(TryGetString(element, "streetAddress"));
            AddPart(TryGetString(element, "addressLocality"));
            AddPart(TryGetString(element, "addressRegion"));
            AddPart(TryGetString(element, "postalCode"));

            if (element.TryGetProperty("addressCountry", out var countryElement))
            {
                string? country = countryElement.ValueKind == JsonValueKind.Object
                    ? TryGetString(countryElement, "name") ?? TryGetString(countryElement, "@id")
                    : TryGetString(countryElement);
                AddPart(country);
            }

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        private string? ExtractPriceFromOffers(JsonElement offersElement)
        {
            string? result = null;

            void Consider(JsonElement offer)
            {
                if (offer.ValueKind == JsonValueKind.Array)
                {
                    foreach (var nested in offer.EnumerateArray())
                        Consider(nested);
                    return;
                }

                if (offer.ValueKind != JsonValueKind.Object)
                    return;

                var priceValue = TryGetString(offer, "price")
                                 ?? TryGetString(offer, "lowPrice")
                                 ?? TryGetString(offer, "highPrice")
                                 ?? TryGetString(offer, "amount");
                if (string.IsNullOrWhiteSpace(priceValue))
                    return;

                var currency = TryGetString(offer, "priceCurrency") ?? TryGetString(offer, "currency");
                var candidate = string.IsNullOrWhiteSpace(currency) ? priceValue : $"{currency} {priceValue}";
                var normalized = NormalizePriceText(candidate);
                if (!string.IsNullOrWhiteSpace(normalized))
                    result ??= normalized;
            }

            switch (offersElement.ValueKind)
            {
                case JsonValueKind.Object:
                    Consider(offersElement);
                    break;
                case JsonValueKind.Array:
                    foreach (var offer in offersElement.EnumerateArray())
                        Consider(offer);
                    break;
            }

            return result;
        }

        private void ApplyGeoToListing(JsonElement geoElement, ExtractedListing item)
        {
            if (geoElement.ValueKind != JsonValueKind.Object)
                return;

            var latitude = CleanText(TryGetString(geoElement, "latitude") ?? TryGetString(geoElement, "lat"));
            var longitude = CleanText(TryGetString(geoElement, "longitude") ?? TryGetString(geoElement, "lng") ?? TryGetString(geoElement, "long"));

            if (string.IsNullOrWhiteSpace(latitude) || string.IsNullOrWhiteSpace(longitude))
                return;

            var coordinates = $"{latitude},{longitude}";
            item.MapLink ??= BuildMapsLink(coordinates);
        }

        private string? TryGetString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (!element.TryGetProperty(propertyName, out var property))
                return null;

            return TryGetString(property);
        }

        private string? TryGetString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetDecimal(out var dec)
                    ? dec.ToString(CultureInfo.InvariantCulture)
                    : element.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            };
        }

        private void AddImageToListing(ExtractedListing item, string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            var normalized = NormalizeUrl(candidate, BookingBaseUrl);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            if (!item.Images.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                item.Images.Add(normalized);
        }

        private List<string> ExtractImages(HtmlDocument doc)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddImage(string? candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                var cleaned = NormalizeUrl(candidate, BookingBaseUrl);
                if (string.IsNullOrWhiteSpace(cleaned))
                    return;

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

        private string? NormalizeUrl(string? input, string? baseHost = null)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var trimmed = input.Trim().Trim('\'', '"');
            if (string.IsNullOrWhiteSpace(trimmed))
                return null;

            try
            {
                trimmed = HtmlEntity.DeEntitize(trimmed);
            }
            catch
            {
                // Ignore decoding issues and keep the trimmed value
            }

            trimmed = trimmed.Replace("\\/", "/").Replace("\\u0026", "&");

            if (trimmed.StartsWith("//"))
                return $"https:{trimmed}";

            if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            if (trimmed.StartsWith("/"))
            {
                if (!string.IsNullOrWhiteSpace(baseHost))
                    return $"{baseHost.TrimEnd('/')}{trimmed}";

                return trimmed;
            }

            if (!string.IsNullOrWhiteSpace(baseHost))
            {
                if (Uri.TryCreate(baseHost, UriKind.Absolute, out var baseUri)
                    && Uri.TryCreate(baseUri, trimmed, out var absolute))
                {
                    return absolute.ToString();
                }
            }

            return trimmed;
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
