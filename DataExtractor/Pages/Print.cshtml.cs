using DataExtractor.Models;
using DataExtractor.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;

namespace DataExtractor.Pages
{
    public class PrintModel : PageModel
    {
        private readonly ListingStore _listingStore;

        public PrintModel(ListingStore listingStore)
        {
            _listingStore = listingStore;
        }

        public IReadOnlyList<ExtractedListing> Items { get; private set; } = Array.Empty<ExtractedListing>();

        public void OnGet()
        {
            Items = _listingStore.GetAll();
        }
    }
}
