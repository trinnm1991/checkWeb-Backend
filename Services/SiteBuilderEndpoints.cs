using VisualMonitor.Web.Models;
namespace VisualMonitor.Web.Services;

public static class SiteBuilderEndpoints
{
    public static void MapSiteBuilder(this WebApplication app)
    {
        var group = app.MapGroup("/api/builder/sites");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (BuilderConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(new { message = "Miền không còn tồn tại." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILogger<SiteBuilderStore>>().LogError(ex, "Site builder storage unavailable");
                return Results.Json(new { message = "Không đọc/ghi được kho bố cục. Bản đang sửa chưa được lưu; hãy xuất JSON để sao lưu." }, statusCode:503);
            }
        });
        group.MapGet("", async (SiteBuilderStore store) => Results.Ok(await store.ListAsync()));
        group.MapPost("", async (BuilderWriteRequest request, SiteBuilderStore store) =>
        {
            var site = await store.SaveAsync(null, request);
            return Results.Created($"/api/builder/sites/{site.Id}", site);
        });
        group.MapPut("/{id}", async (string id, BuilderWriteRequest request, SiteBuilderStore store) => Results.Ok(await store.SaveAsync(id, request)));
        group.MapDelete("/{id}", async (string id, long revision, SiteBuilderStore store) =>
        {
            await store.DeleteAsync(id, revision); return Results.Ok(new { message = "Đã xóa cấu hình miền. Không thay đổi DNS hoặc website bên ngoài." });
        });
    }
}
