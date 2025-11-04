using DataExtractor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using System.Collections.Generic;

namespace DataExtractor.Pages
{
    public class ResultsModel : PageModel
    {
        private readonly ILogger<ResultsModel> _logger;

        public ResultsModel(ILogger<ResultsModel> logger)
        {
            _logger = logger;
        }

        [TempData]
        public string? ExtractedItemsJson { get; set; }

        public List<ExtractedListing> Items { get; private set; } = new();

        public void OnGet()
        {
            if (string.IsNullOrWhiteSpace(ExtractedItemsJson))
            {
                ModelState.AddModelError(string.Empty, "No extracted listings were found. Please submit one or more Booking.com URLs.");
                return;
            }

            try
            {
                var items = JsonSerializer.Deserialize<List<ExtractedListing>>(ExtractedItemsJson);
                if (items != null)
                {
                    Items = items;
                }
                TempData.Keep(nameof(ExtractedItemsJson));
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize extracted listings");
                ModelState.AddModelError(string.Empty, "We couldn't display the extracted listings. Please try again.");
            }
        }
    }
}
