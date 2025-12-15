using MachineLinkConfig.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
namespace MachineLinkConfig

{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var cs = builder.Configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("Connection string 'Default' not found.");

            builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(cs));

            // Add services to the container.
            builder.Services.AddControllersWithViews();


            var app = builder.Build();
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthorization();

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");
            app.MapGet("/health", async (AppDbContext db) =>
            {
                try
                {
                    var canConnect = await db.Database.CanConnectAsync();
                    return Results.Ok(new { ok = true, db = canConnect });
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
            });

            app.Run();
        }
    }
}
