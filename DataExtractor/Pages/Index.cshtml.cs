using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using HtmlAgilityPack;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

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
                    if (!string.IsNullOrWhiteSpace(ogTitle)) item.Title = ogTitle.Trim();
                    else item.Title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? "";

                    // Description
                    var desc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", null);
                    if (!string.IsNullOrWhiteSpace(desc)) item.Description = desc.Trim();

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
                    var rating = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'b5cd09854e')]")?.InnerText
                                 ?? doc.DocumentNode.SelectSingleNode("//span[@aria-label and contains(.,'score')]")?.InnerText;
                    if (!string.IsNullOrWhiteSpace(rating)) item.Rating = HtmlEntity.DeEntitize(rating).Trim();
                    else
                    {
                        var metaRating = doc.DocumentNode.SelectSingleNode("//meta[@itemprop='ratingValue']")?.GetAttributeValue("content", null);
                        if (!string.IsNullOrWhiteSpace(metaRating)) item.Rating = metaRating.Trim();
                    }

                    // Price: try price display class or meta
                    var priceNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class,'bui-price-display__value')]")
                                      ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'price')]")
                                      ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'price')]");
                    if (priceNode != null) item.Price = HtmlEntity.DeEntitize(priceNode.InnerText).Trim();
                    else
                    {
                        var metaPrice = doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:amount']")?.GetAttributeValue("content", null);
                        if (!string.IsNullOrWhiteSpace(metaPrice)) item.Price = metaPrice.Trim();
                    }

                    // Location/address
                    var loc = doc.DocumentNode.SelectSingleNode("//span[@data-testid='address']")?.InnerText
                              ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'hp_address_subtitle')] ")?.InnerText
                              ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'address')]")?.InnerText;
                    if (!string.IsNullOrWhiteSpace(loc)) item.Location = HtmlEntity.DeEntitize(loc).Trim();

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
                    page.Margin(20);
                    page.Size(PageSizes.A4);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(12));

                    page.Header().Text("Booking.com - Extraction Summary").SemiBold().FontSize(18).AlignCenter();

                    page.Content().PaddingVertical(10).Column(col =>
                    {
                        foreach (var it in items)
                        {
                            col.Item().Element(c => RenderItem(c, it));
                            // horizontal separator with padding
                            col.Item().PaddingVertical(5).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                        }
                    });

                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span("Generated: ").SemiBold();
                        x.Span(DateTime.UtcNow.ToString("u"));
                    });
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
            container.Padding(5).Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeColumn().Column(c2 =>
                    {
                        c2.Item().Text(item.Title ?? "").SemiBold().FontSize(14);
                        if (!string.IsNullOrWhiteSpace(item.Location)) c2.Item().Text(item.Location).FontSize(10).FontColor(Colors.Grey.Darken1);
                        if (!string.IsNullOrWhiteSpace(item.Price)) c2.Item().Text($"Price: {item.Price}").FontSize(11);
                        if (!string.IsNullOrWhiteSpace(item.Rating)) c2.Item().Text($"Rating: {item.Rating}").FontSize(11);
                        if (!string.IsNullOrWhiteSpace(item.Description)) c2.Item().Text(item.Description).FontSize(9).FontColor(Colors.Grey.Darken2);
                        c2.Item().Text(item.Url).FontSize(9).FontColor(Colors.Blue.Medium);
                    });

                    if (item.Images.Count > 0)
                    {
                        var imgUrl = item.Images[0];
                        try
                        {
                            using var http = new HttpClient();
                            var bytes = http.GetByteArrayAsync(imgUrl).GetAwaiter().GetResult();
                            row.ConstantColumn(120).Height(80).Image(bytes);
                        }
                        catch (Exception imgEx)
                        {
                            // if image loading fails, log and render placeholder
                            _logger.LogDebug(imgEx, "Failed to load image {ImageUrl}", imgUrl);
                            row.ConstantColumn(120).Height(80).Placeholder();
                        }
                    }
                });
            });
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
