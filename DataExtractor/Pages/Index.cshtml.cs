using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using HtmlAgilityPack;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DataExtractor.Pages
{
    public class IndexModel : PageModel
    {
        private static readonly HttpClient ImageHttpClient = new();

        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }

        [BindProperty]
        public string? Urls { get; set; }

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

            var items = new List<ExtractedItem>();

            using var http = new HttpClient();
            foreach (var url in urlList)
            {
                try
                {
                    var html = await http.GetStringAsync(url);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var item = new ExtractedItem { Url = url };

                    // Title: try og:title then title tag
                    var ogTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", null);
                    if (!string.IsNullOrWhiteSpace(ogTitle)) item.Title = CleanText(ogTitle);
                    else item.Title = CleanText(doc.DocumentNode.SelectSingleNode("//title")?.InnerText) ?? string.Empty;

                    // Description
                    var desc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", null)
                               ?? doc.DocumentNode.SelectSingleNode("//div[@data-testid='property-description']//p")?.InnerText
                               ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'hotel_desc')]")?.InnerText;
                    item.Description = CleanText(desc);

                    // Image: og:image
                    var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null);
                    if (!string.IsNullOrWhiteSpace(ogImage)) item.Images.Add(ogImage.Trim());
                    else
                    {
                        // try to find first image in gallery
                        var imgNode = doc.DocumentNode.SelectSingleNode("//img[@data-testid='hero-image']")
                                     ?? doc.DocumentNode.SelectSingleNode("//img[contains(@class,'hotel_image')]")
                                     ?? doc.DocumentNode.SelectSingleNode("//img[1]");
                        var src = imgNode?.GetAttributeValue("src", null) ?? imgNode?.GetAttributeValue("data-src", null);
                        if (!string.IsNullOrWhiteSpace(src)) item.Images.Add(src);
                    }

                    // Rating: try common selectors
                    item.Rating = ExtractRating(doc);

                    // Price: try price display class or meta
                    item.Price = ExtractPrice(doc);

                    // Location/address
                    var loc = doc.DocumentNode.SelectSingleNode("//span[@data-testid='address']")?.InnerText
                              ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'hp_address_subtitle')] ")?.InnerText
                              ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'address')]")?.InnerText;
                    item.Location = CleanText(loc);

                    items.Add(item);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract {Url}", url);
                    items.Add(new ExtractedItem { Url = url, Title = "(Failed to fetch)", Description = ex.Message });
                }
            }

            // Generate PDF
            byte[] pdfBytes;
            try
            {
                pdfBytes = GeneratePdf(items);
            }
            catch (Exception ex)
            {
                // Log full details so we can diagnose the unnamed exception
                _logger.LogError(ex, "PDF generation failed");

                // Surface a helpful message to the page (avoid exposing sensitive details in production)
                ModelState.AddModelError(string.Empty, "PDF generation failed: " + ex.Message + (ex.InnerException != null ? " - " + ex.InnerException.Message : string.Empty));
                return Page();
            }

            var fileName = "extracted_booking_summary.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        private byte[] GeneratePdf(List<ExtractedItem> items)
        {
            var ms = new MemoryStream();

            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(25);
                    page.Size(PageSizes.A4);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(11));

                    page.Header().PaddingBottom(10).Column(header =>
                    {
                        header.Item().Text("Booking.com - Extraction Summary").SemiBold().FontSize(20).AlignCenter();
                        header.Item().AlignCenter().Text(text =>
                        {
                            text.Span("Listings exported: ").SemiBold();
                            text.Span(items.Count.ToString());
                        });
                    });

                    page.Content().Padding(15).Column(col =>
                    {
                        col.Spacing(15);

                        col.Item().Text(text =>
                        {
                            text.Span("Generated on ").SemiBold();
                            text.Span(DateTime.UtcNow.ToString("dddd, dd MMMM yyyy 'at' HH:mm 'UTC'"));
                        }).FontColor(Colors.Grey.Darken1);

                        foreach (var it in items)
                        {
                            col.Item().Element(c => RenderItem(c, it));
                        }
                    });

                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span("Data extracted from Booking.com | ");
                        x.Span(DateTime.UtcNow.ToString("u"));
                    }).FontSize(9).FontColor(Colors.Grey.Darken1);
                });
            });

            try
            {
                document.GeneratePdf(ms);
            }
            catch (Exception ex)
            {
                // Log more details and rethrow to be handled upstream
                _logger.LogError(ex, "Exception while generating PDF: {Message}", ex.Message);

                // Some exceptions may have inner exceptions with clearer messages
                var details = ex.Message + (ex.InnerException != null ? " | Inner: " + ex.InnerException.Message : string.Empty);
                throw new InvalidOperationException("PDF generation failed: " + details, ex);
            }

            return ms.ToArray();
        }

        private void RenderItem(IContainer container, ExtractedItem item)
        {
            container
                .Padding(16)
                .Border(1)
                .BorderColor(Colors.Grey.Lighten3)
                .Background(Colors.White)
                .Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Row(row =>
                    {
                        row.RelativeColumn().Column(info =>
                        {
                            info.Spacing(6);

                            info.Item().Text(item.Title ?? "").SemiBold().FontSize(16);

                            if (!string.IsNullOrWhiteSpace(item.Location))
                            {
                                info.Item().Text(text =>
                                {
                                    text.Span("Location: ").SemiBold();
                                    text.Span(item.Location);
                                }).FontSize(11).FontColor(Colors.Grey.Darken1);
                            }

                            if (!string.IsNullOrWhiteSpace(item.Price) || !string.IsNullOrWhiteSpace(item.Rating))
                            {
                                info.Item().Row(tags =>
                                {
                                    tags.Spacing(10);

                                    if (!string.IsNullOrWhiteSpace(item.Price))
                                    {
                                        tags.AutoItem().PaddingVertical(6).PaddingHorizontal(10)
                                            .Border(1)
                                            .BorderColor(Colors.Green.Darken1.WithAlpha(0.3f))
                                            .Background(Colors.Green.Lighten4)
                                            .Text(text =>
                                            {
                                                text.Span("Price: ").SemiBold();
                                                text.Span(item.Price);
                                            }).FontSize(11);
                                    }

                                    if (!string.IsNullOrWhiteSpace(item.Rating))
                                    {
                                        tags.AutoItem().PaddingVertical(6).PaddingHorizontal(10)
                                            .Border(1)
                                            .BorderColor(Colors.Blue.Darken1.WithAlpha(0.3f))
                                            .Background(Colors.Blue.Lighten4)
                                            .Text(text =>
                                            {
                                                text.Span("Rating: ").SemiBold();
                                                text.Span(item.Rating);
                                            }).FontSize(11);
                                    }
                                });
                            }

                            if (!string.IsNullOrWhiteSpace(item.Description))
                            {
                                info.Item().Text(item.Description).FontSize(10).FontColor(Colors.Grey.Darken2);
                            }

                            info.Item().Text(text =>
                            {
                                text.Span("Source: ").SemiBold().FontSize(9);
                                text.Span(item.Url).FontColor(Colors.Blue.Medium).FontSize(9);
                            });
                        });

                        if (item.Images.Count > 0)
                        {
                            var imgUrl = item.Images[0];
                            try
                            {
                                var bytes = ImageHttpClient.GetByteArrayAsync(imgUrl).GetAwaiter().GetResult();
                                row.ConstantColumn(140).Height(100).Image(bytes).FitArea();
                            }
                            catch (Exception imgEx)
                            {
                                // if image loading fails, log and render placeholder
                                _logger.LogDebug(imgEx, "Failed to load image {ImageUrl}", imgUrl);
                                row.ConstantColumn(140).Height(100).Placeholder();
                            }
                        }
                        else
                        {
                            row.ConstantColumn(140).Height(100).Placeholder();
                        }
                    });

                    col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten3);
                });
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
                "//span[@data-testid='price-summary']",
                "//div[contains(@class,'prco-valign-center-helper')]",
                "//span[contains(@class,'prco-inline-block-maker-helper')]",
                "//span[contains(@class,'bui-price-display__value')]",
                "//div[contains(@class,'bui-price-display__value')]"
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

        private class ExtractedItem
        {
            public string Url { get; set; } = string.Empty;
            public string? Title { get; set; }
            public string? Description { get; set; }
            public List<string> Images { get; set; } = new();
            public string? Price { get; set; }
            public string? Rating { get; set; }
            public string? Location { get; set; }
        }
    }
}
