var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

// Booking.com pages sit behind a JavaScript bot challenge, so they are fetched
// through headless Chromium rather than HttpClient. Singleton so the browser
// instance is launched once and reused.
builder.Services.AddSingleton<DataExtractor.Services.BrowserPageFetcher>();
builder.Services.AddSingleton<DataExtractor.Services.ListingStore>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}


app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
