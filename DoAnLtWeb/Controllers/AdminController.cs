using Docnet.Core;
using Docnet.Core.Models;
using DoAnLtWeb.Data;
using DoAnLtWeb.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;

namespace DoAnLtWeb.Controllers
{
    [Authorize]
    public class AdminController : Controller
    {
        private record GrpTransform(double OffX, double OffY, double ScX, double ScY);

        private readonly AppDbContext _context;
        private readonly UserManager<User> _userManager;

        public AdminController(AppDbContext context, UserManager<User> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        [HttpGet]
        public async Task<IActionResult> Dashboard()
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            // Compute statistics
            ViewBag.TotalUsers = await _context.Users.CountAsync();
            ViewBag.TotalVipUsers = await _context.Users.CountAsync(u => u.IsVip);
            ViewBag.TotalTemplates = await _context.Presentations.CountAsync(p => p.IsTemplate);
            ViewBag.TotalUserProjects = await _context.Presentations.CountAsync(p => !p.IsTemplate);
            ViewBag.TotalTransactions = await _context.PaymentTransactions.CountAsync();
            ViewBag.PendingTransactions = await _context.PaymentTransactions.CountAsync(t => t.Status == "Pending");
            ViewBag.ConfirmedTransactions = await _context.PaymentTransactions.CountAsync(t => t.Status == "Confirmed");
            ViewBag.TotalRevenue = await _context.PaymentTransactions
                .Where(t => t.Status == "Confirmed")
                .SumAsync(t => t.Amount);

            // Recent users list
            var recentUsers = await _context.Users
                .OrderByDescending(u => u.Id)
                .Take(5)
                .ToListAsync();

            // Recent transactions list
            var recentTxns = await _context.PaymentTransactions
                .Include(t => t.User)
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .ToListAsync();
            ViewBag.RecentTxns = recentTxns;

            // Category slide counts
            var templates = await _context.Presentations
                .Where(p => p.IsTemplate)
                .ToListAsync();
            var categories = templates.GroupBy(p => p.Category)
                .Select(g => new { Category = string.IsNullOrEmpty(g.Key) ? "Khác" : g.Key, Count = g.Count() })
                .ToList();
            ViewBag.Categories = categories;

            return View(recentUsers);
        }

        [HttpGet]
        public async Task<IActionResult> Payments()
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            var transactions = await _context.PaymentTransactions
                .Include(t => t.User)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            return View(transactions);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPayment(int id)
        {
            if (!IsAdmin()) return Forbid();

            var txn = await _context.PaymentTransactions
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (txn == null) return NotFound();
            if (txn.Status == "Confirmed")
                return RedirectToAction(nameof(Payments));

            txn.Status = "Confirmed";
            txn.ConfirmedAt = DateTime.UtcNow;
            txn.Note = "Xac nhan boi admin";

            if (txn.User != null)
            {
                txn.User.IsVip = true;
                txn.User.VipPlanName = txn.PlanName;
                txn.User.VipExpiresAt = DateTime.UtcNow.AddDays(30);
            }

            await _context.SaveChangesAsync();
            TempData["AdminSuccess"] = $"Đã xác nhận giao dịch {txn.PaymentCode}. Người dùng đã được nâng cấp lên VIP.";
            return RedirectToAction(nameof(Payments));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectPayment(int id)
        {
            if (!IsAdmin()) return Forbid();

            var txn = await _context.PaymentTransactions
                .FirstOrDefaultAsync(t => t.Id == id);

            if (txn == null) return NotFound();

            txn.Status = "Rejected";
            txn.Note = "Từ chối bởi quản trị viên";
            await _context.SaveChangesAsync();

            TempData["AdminSuccess"] = $"Đã từ chối giao dịch {txn.PaymentCode}.";
            return RedirectToAction(nameof(Payments));
        }

        [HttpGet]
        public async Task<IActionResult> Templates()
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            var templates = await _context.Presentations
                .Include(p => p.Slides)
                .Where(p => p.IsTemplate)
                .OrderBy(p => p.Title)
                .ToListAsync();

            return View(templates);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTemplate(int id)
        {
            if (!IsAdmin()) return Forbid();

            var tpl = await _context.Presentations
                .Include(p => p.Slides)
                .FirstOrDefaultAsync(p => p.Id == id && p.IsTemplate);

            if (tpl == null) return NotFound();

            _context.Presentations.Remove(tpl);
            await _context.SaveChangesAsync();

            TempData["AdminSuccess"] = $"Đã xóa mẫu: {tpl.Title}";
            return RedirectToAction(nameof(Templates));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTrashTemplates()
        {
            if (!IsAdmin()) return Forbid();

            // Load all templates, then filter in-memory using regex so SQL can't trip on bracket chars
            var allTemplates = await _context.Presentations
                .Include(p => p.Slides)
                .Where(p => p.IsTemplate)
                .ToListAsync();

            var trashKeywords = new[] { "China", "CQU", "Telecom", "CMB", "Chongqing", "ChinaUni" };
            var trash = allTemplates.Where(p =>
                !p.Slides.Any() ||
                trashKeywords.Any(kw => p.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                System.Text.RegularExpressions.Regex.IsMatch(p.Title, @"\[\d+")  // any [123] numbering
            ).ToList();

            _context.Presentations.RemoveRange(trash);
            await _context.SaveChangesAsync();

            TempData["AdminSuccess"] = $"Đã dọn sạch {trash.Count} mẫu rác.";
            return RedirectToAction(nameof(Templates));
        }

        private bool IsAdmin()
        {
            if (User.IsInRole("Admin")) return true;
            var email = User.FindFirstValue(ClaimTypes.Email)
                        ?? User.FindFirstValue(ClaimTypes.Name)
                        ?? User.Identity?.Name;
            return email == "admin@gmail.com";
        }

        [HttpGet]
        public async Task<IActionResult> TemplateSubmissions()
        {
            if (!IsAdmin()) return Forbid();
            var items = await _context.TemplateSubmissions
                .Include(s => s.User)
                .Include(s => s.Presentation)
                    .ThenInclude(p => p!.Slides)
                .OrderByDescending(s => s.SubmittedAt)
                .ToListAsync();
            return View(items);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveSubmission(int id, string? adminNote)
        {
            if (!IsAdmin()) return Forbid();
            var sub = await _context.TemplateSubmissions
                .Include(s => s.Presentation)
                    .ThenInclude(p => p!.Slides)
                .FirstOrDefaultAsync(s => s.Id == id);
            if (sub == null) return NotFound();
            if (sub.Presentation == null) { sub.Status = TemplateSubmissionStatus.Rejected; await _context.SaveChangesAsync(); return RedirectToAction(nameof(TemplateSubmissions)); }

            // Clone the user's deck into a new admin-owned template so the user can keep editing
            // their own copy without affecting the published version.
            var adminUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == "admin@gmail.com");
            if (adminUser == null) return BadRequest("Admin user missing.");

            var tpl = new Presentation
            {
                Title = sub.ProposedTitle,
                Category = $"Slidify Cộng đồng / {sub.ProposedCategory}",
                UserId = adminUser.Id,
                IsTemplate = true,
                IsPremiumTemplate = false,
                ThumbnailUrl = sub.Presentation.ThumbnailUrl,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            foreach (var s in sub.Presentation.Slides.OrderBy(x => x.PageNumber))
            {
                tpl.Slides.Add(new Slide
                {
                    PageNumber = s.PageNumber,
                    BackgroundColor = s.BackgroundColor,
                    BackgroundImage = s.BackgroundImage,
                    ElementsJson = s.ElementsJson
                });
            }
            _context.Presentations.Add(tpl);

            sub.Status = TemplateSubmissionStatus.Approved;
            sub.AdminNote = adminNote ?? string.Empty;
            sub.ReviewedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(TemplateSubmissions));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectSubmission(int id, string? adminNote)
        {
            if (!IsAdmin()) return Forbid();
            var sub = await _context.TemplateSubmissions.FirstOrDefaultAsync(s => s.Id == id);
            if (sub == null) return NotFound();
            sub.Status = TemplateSubmissionStatus.Rejected;
            sub.AdminNote = adminNote ?? string.Empty;
            sub.ReviewedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(TemplateSubmissions));
        }

        // ─── PPTX IMPORTER ───────────────────────────────────────────────────────

        [HttpGet]
        public IActionResult ImportPptx()
        {
            if (!IsAdmin()) return RedirectToAction("Index", "Home");

            var slideMauDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "slide-mau");
            var files = Directory.Exists(slideMauDir)
                ? Directory.GetFiles(slideMauDir, "*.pptx").Select(Path.GetFileName).ToList()
                : new List<string?>();

            ViewBag.PptxFiles = files;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportPptx(string fileName, string? category, bool isPremium = false)
        {
            if (!IsAdmin()) return Forbid();

            var slideMauDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "slide-mau");
            var filePath = Path.Combine(slideMauDir, fileName);

            if (!System.IO.File.Exists(filePath) || !filePath.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                return BadRequest("File không tồn tại hoặc không phải PPTX.");

            var adminUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == "admin@slidify.com");
            if (adminUser == null) return BadRequest("Cần tạo admin user trước.");

            var title = Path.GetFileNameWithoutExtension(fileName);
            var slides = ParsePptxToFabricSlides(filePath);

            var presentation = new Presentation
            {
                Title = title,
                UserId = adminUser.Id,
                IsTemplate = true,
                IsPremiumTemplate = isPremium,
                Category = category ?? "Imported",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            foreach (var (fabricJson, bgColor, bgImage, idx) in slides)
            {
                presentation.Slides.Add(new Slide
                {
                    PageNumber = idx + 1,
                    BackgroundColor = bgColor,
                    BackgroundImage = bgImage,
                    ElementsJson = fabricJson
                });
            }

            _context.Presentations.Add(presentation);
            await _context.SaveChangesAsync();

            TempData["AdminSuccess"] = $"Đã nhập mẫu '{title}' thành công ({slides.Count} trang).";
            return RedirectToAction(nameof(ImportPptx));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportAllPptx(string? category, bool isPremium = false)
        {
            if (!IsAdmin()) return Forbid();

            var slideMauDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "slide-mau");
            if (!Directory.Exists(slideMauDir))
            {
                TempData["AdminError"] = "Thư mục slide-mau không tồn tại.";
                return RedirectToAction(nameof(ImportPptx));
            }

            var adminUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == "admin@slidify.com");
            if (adminUser == null) { TempData["AdminError"] = "Cần tạo tài khoản quản trị trước."; return RedirectToAction(nameof(ImportPptx)); }

            var files = Directory.GetFiles(slideMauDir, "*.pptx");
            int count = 0;
            foreach (var filePath in files)
            {
                var title = Path.GetFileNameWithoutExtension(filePath);
                // Skip already imported
                if (await _context.Presentations.AnyAsync(p => p.IsTemplate && p.Title == title)) continue;

                var slides = ParsePptxToFabricSlides(filePath);
                var presentation = new Presentation
                {
                    Title = title,
                    UserId = adminUser.Id,
                    IsTemplate = true,
                    IsPremiumTemplate = isPremium,
                    Category = category ?? "Imported",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                foreach (var (fabricJson, bgColor, bgImage, idx) in slides)
                {
                    presentation.Slides.Add(new Slide
                    {
                        PageNumber = idx + 1,
                        BackgroundColor = bgColor,
                        BackgroundImage = bgImage,
                        ElementsJson = fabricJson
                    });
                }
                _context.Presentations.Add(presentation);
                count++;
            }

            await _context.SaveChangesAsync();
            TempData["AdminSuccess"] = $"Đã nhập thành công {count} tệp slide PPTX vào danh sách mẫu.";
            return RedirectToAction(nameof(ImportPptx));
        }

        // ── LibreOffice: convert PPTX → PDF, returns pdf path or null ────────────
        private static string? CreatePptxCopyWithoutText(string pptxPath)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"slidify_notext_{Guid.NewGuid():N}.pptx");
            try
            {
                System.IO.File.Copy(pptxPath, tempPath, true);
                XNamespace aNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
                using var archive = ZipFile.Open(tempPath, ZipArchiveMode.Update);
                var xmlEntries = archive.Entries
                    .Where(e => e.FullName.StartsWith("ppt/slides/") && e.FullName.EndsWith(".xml") && !e.FullName.Contains("/_rels/"))
                    .ToList();

                foreach (var entry in xmlEntries)
                {
                    XDocument doc;
                    using (var stream = entry.Open())
                        doc = XDocument.Load(stream);

                    foreach (var textNode in doc.Descendants(aNs + "t"))
                        textNode.Value = "";

                    using var writeStream = entry.Open();
                    writeStream.SetLength(0);
                    doc.Save(writeStream, SaveOptions.DisableFormatting);
                }

                return tempPath;
            }
            catch
            {
                try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
                return null;
            }
        }

        private static List<string> RenderPptxToSlideImagesWithPowerPoint(string pptxPath, string importedDir)
        {
            var urls = new List<string>();
            var exportDir = Path.Combine(Path.GetTempPath(), "slidify_ppt_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(exportDir);

            object? app = null;
            object? presentation = null;
            try
            {
                var pptType = Type.GetTypeFromProgID("PowerPoint.Application");
                if (pptType == null) return urls;

                app = Activator.CreateInstance(pptType);
                var presentations = app!.GetType().InvokeMember("Presentations", System.Reflection.BindingFlags.GetProperty, null, app, null);
                presentation = presentations!.GetType().InvokeMember("Open", System.Reflection.BindingFlags.InvokeMethod, null, presentations, new object[] { pptxPath, true, true, false });
                presentation!.GetType().InvokeMember("SaveAs", System.Reflection.BindingFlags.InvokeMethod, null, presentation, new object[] { exportDir, 18 });

                foreach (var pngPath in Directory.GetFiles(exportDir, "*.PNG").Concat(Directory.GetFiles(exportDir, "*.png")).OrderBy(NaturalSlideImageOrder))
                {
                    var fileName = $"{Guid.NewGuid()}.png";
                    var dest = Path.Combine(importedDir, fileName);
                    System.IO.File.Copy(pngPath, dest, true);
                    urls.Add($"/uploads/imported/{fileName}");
                }
            }
            catch { }
            finally
            {
                try { presentation?.GetType().InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, presentation, null); } catch { }
                try { app?.GetType().InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, app, null); } catch { }
                try { Directory.Delete(exportDir, true); } catch { }
            }

            return urls;
        }

        private static int NaturalSlideImageOrder(string path)
        {
            var digits = new string(Path.GetFileNameWithoutExtension(path).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : int.MaxValue;
        }

        private static string? ConvertToPdf(string pptxPath, string outDir)
        {
            var libreOfficePath = @"C:\Program Files\LibreOffice\program\soffice.exe";
            if (!System.IO.File.Exists(libreOfficePath)) return null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = libreOfficePath,
                    WorkingDirectory = outDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("--headless");
                psi.ArgumentList.Add("--convert-to");
                psi.ArgumentList.Add("pdf");
                psi.ArgumentList.Add("--outdir");
                psi.ArgumentList.Add(outDir);
                psi.ArgumentList.Add(pptxPath);

                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit(120000);
                var expectedPdf = Path.Combine(outDir, Path.GetFileNameWithoutExtension(pptxPath) + ".pdf");
                if (System.IO.File.Exists(expectedPdf)) return expectedPdf;

                return Directory.GetFiles(outDir, "*.pdf")
                    .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        // ── Docnet: render each PDF page → PNG, returns list of /uploads/... URLs ─
        private static List<string> RenderPdfToSlideImages(string pdfPath, string importedDir)
        {
            var urls = new List<string>();
            try
            {
                using var lib = DocLib.Instance;
                using var docReader = lib.GetDocReader(pdfPath, new PageDimensions(1920, 1080));
                int pageCount = docReader.GetPageCount();
                for (int i = 0; i < pageCount; i++)
                {
                    using var pageReader = docReader.GetPageReader(i);
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    var rawBytes = pageReader.GetImage();
                    // Docnet returns BGRA, convert to PNG via ImageSharp
                    using var img = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(rawBytes, w, h);
                    var fileName = $"{Guid.NewGuid()}.png";
                    var dest = Path.Combine(importedDir, fileName);
                    img.SaveAsPng(dest);
                    urls.Add($"/uploads/imported/{fileName}");
                }
            }
            catch { }
            return urls;
        }

        public static List<(string fabricJson, string bgColor, string bgImage, int index)> ParsePptxToFabricSlides(string filePath)
        {
            var result = new List<(string, string, string, int)>();

            const double canvasW = 760.0;
            const double canvasH = 428.0;

            XNamespace aNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
            XNamespace pNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
            XNamespace rNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace relsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

            string importedDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "imported");
            if (!Directory.Exists(importedDir)) Directory.CreateDirectory(importedDir);

            List<string> slideImageUrls = new();
            var noTextPptxPath = CreatePptxCopyWithoutText(filePath);
            try
            {
                if (noTextPptxPath != null)
                    slideImageUrls = RenderPptxToSlideImagesWithPowerPoint(noTextPptxPath, importedDir);

                if (slideImageUrls.Count == 0)
                    slideImageUrls = RenderPptxToSlideImagesWithPowerPoint(filePath, importedDir);

                if (slideImageUrls.Count == 0)
                {
                    var tempPdfDir = Path.Combine(Path.GetTempPath(), "slidify_pdf_" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(tempPdfDir);
                    try
                    {
                        var pdfPath = ConvertToPdf(noTextPptxPath ?? filePath, tempPdfDir);
                        if (pdfPath != null)
                            slideImageUrls = RenderPdfToSlideImages(pdfPath, importedDir);
                    }
                    finally
                    {
                        try { Directory.Delete(tempPdfDir, true); } catch { }
                    }
                }
            }
            finally
            {
                try { if (noTextPptxPath != null) System.IO.File.Delete(noTextPptxPath); } catch { }
            }

            using var zip = ZipFile.OpenRead(filePath);

            // ── Read actual slide dimensions from presentation.xml ─────────────────
            double slideEmuW = 9144000.0;
            double slideEmuH = 5143500.0;
            var presEntry = zip.GetEntry("ppt/presentation.xml");
            if (presEntry != null)
            {
                using var ps = presEntry.Open();
                var presDoc = XDocument.Load(ps);
                var sldSz = presDoc.Descendants(pNs + "sldSz").FirstOrDefault();
                if (sldSz != null)
                {
                    if (double.TryParse(sldSz.Attribute("cx")?.Value, out var pcx) && pcx > 0) slideEmuW = pcx;
                    if (double.TryParse(sldSz.Attribute("cy")?.Value, out var pcy) && pcy > 0) slideEmuH = pcy;
                }
            }
            double scaleX = canvasW / slideEmuW;
            double scaleY = canvasH / slideEmuH;

            // ── Load theme colors (ppt/theme/theme1.xml) ──────────────────────────
            var themeColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var themeEntry = zip.GetEntry("ppt/theme/theme1.xml");
            if (themeEntry != null)
            {
                using var ts = themeEntry.Open();
                var themeDoc = XDocument.Load(ts);
                var colorMap = new[] { "dk1", "dk2", "lt1", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
                foreach (var colorName in colorMap)
                {
                    var colorEl = themeDoc.Descendants(aNs + colorName).FirstOrDefault();
                    if (colorEl == null) continue;
                    var srgb = colorEl.Descendants(aNs + "srgbClr").FirstOrDefault()?.Attribute("val")?.Value;
                    if (!string.IsNullOrEmpty(srgb)) themeColors[colorName] = "#" + srgb;
                    var sysClr = colorEl.Descendants(aNs + "sysClr").FirstOrDefault()?.Attribute("lastClr")?.Value;
                    if (sysClr != null && !themeColors.ContainsKey(colorName)) themeColors[colorName] = "#" + sysClr;
                }
                // Add common scheme name aliases
                if (themeColors.TryGetValue("dk1", out var dk1)) { themeColors["tx1"] = dk1; themeColors["text1"] = dk1; }
                if (themeColors.TryGetValue("dk2", out var dk2)) { themeColors["tx2"] = dk2; themeColors["text2"] = dk2; }
                if (themeColors.TryGetValue("lt1", out var lt1)) { themeColors["bg1"] = lt1; themeColors["background1"] = lt1; }
                if (themeColors.TryGetValue("lt2", out var lt2)) { themeColors["bg2"] = lt2; themeColors["background2"] = lt2; }
            }

            // ── Load slide layout/master objects (for inherited backgrounds) ──────
            // We load master rels to get layout->master chain, but keep it simple:
            // just collect slide layout element lists per layout file.
            var layoutObjects = new Dictionary<string, List<XElement>>(); // layoutPath -> sp/pic elements
            var layoutBgColors = new Dictionary<string, string>();
            var layoutBgImages = new Dictionary<string, string>();

            // ── Helper: resolve color from fill element, returns CSS color string ──
            string ResolveColor(XElement? fillEl, string fallback = "#e2e8f0")
            {
                if (fillEl == null) return fallback;

                string ApplyAlpha(string hex, XElement? colorEl)
                {
                    // alpha child: val 0=transparent, 100000=opaque
                    var alphaEl = colorEl?.Element(aNs + "alpha");
                    if (alphaEl == null) return hex;
                    if (!int.TryParse(alphaEl.Attribute("val")?.Value, out var alphaVal)) return hex;
                    double opacity = alphaVal / 100000.0;
                    if (opacity >= 0.99) return hex;
                    if (opacity <= 0.01) return "transparent";
                    // Convert to rgba
                    hex = hex.TrimStart('#');
                    if (hex.Length == 6)
                    {
                        int r = Convert.ToInt32(hex[..2], 16);
                        int g = Convert.ToInt32(hex[2..4], 16);
                        int b = Convert.ToInt32(hex[4..6], 16);
                        return $"rgba({r},{g},{b},{Math.Round(opacity, 2)})";
                    }
                    return "#" + hex;
                }

                var srgbEl = fillEl.Descendants(aNs + "srgbClr").FirstOrDefault();
                if (srgbEl != null)
                {
                    var val = srgbEl.Attribute("val")?.Value;
                    if (!string.IsNullOrEmpty(val)) return ApplyAlpha("#" + val, srgbEl);
                }
                var schemeEl = fillEl.Descendants(aNs + "schemeClr").FirstOrDefault();
                if (schemeEl != null)
                {
                    var scheme = schemeEl.Attribute("val")?.Value;
                    if (!string.IsNullOrEmpty(scheme) && themeColors.TryGetValue(scheme, out var tc))
                        return ApplyAlpha(tc, schemeEl);
                    // lumMod/lumOff tints — approximate: return base color
                    if (!string.IsNullOrEmpty(scheme) && themeColors.TryGetValue(scheme.Replace("Ref", ""), out var tc2))
                        return ApplyAlpha(tc2, schemeEl);
                }
                var sysClrEl = fillEl.Descendants(aNs + "sysClr").FirstOrDefault();
                if (sysClrEl != null)
                {
                    var lastClr = sysClrEl.Attribute("lastClr")?.Value;
                    if (!string.IsNullOrEmpty(lastClr)) return ApplyAlpha("#" + lastClr, sysClrEl);
                }
                var prstClrEl = fillEl.Descendants(aNs + "prstClr").FirstOrDefault();
                if (prstClrEl != null)
                {
                    var prstVal = prstClrEl.Attribute("val")?.Value;
                    if (!string.IsNullOrEmpty(prstVal)) return PrstColorToHex(prstVal);
                }
                return fallback;
            }

            // ── Helper: extract image from zip entry to /uploads/imported/ ────────
            string? ExtractImage(string resolvedPath)
            {
                var imgEntry = zip.GetEntry(resolvedPath);
                if (imgEntry == null) return null;
                var ext = Path.GetExtension(resolvedPath);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var fileName = $"{Guid.NewGuid()}{ext}";
                var dest = Path.Combine(importedDir, fileName);
                using var src2 = imgEntry.Open();
                using var dst = new FileStream(dest, FileMode.Create);
                src2.CopyTo(dst);
                return $"/uploads/imported/{fileName}";
            }

            // ── Helper: resolve media target path ─────────────────────────────────
            string ResolveMediaPath(string target)
            {
                if (target.StartsWith("../")) return "ppt/" + target[3..];
                if (target.StartsWith("media/")) return "ppt/" + target;
                return target;
            }

            // ── Helper: load rels dict for a zip entry ────────────────────────────
            Dictionary<string, string> LoadRels(ZipArchiveEntry entry)
            {
                var d = new Dictionary<string, string>();
                var dir = Path.GetDirectoryName(entry.FullName)?.Replace('\\', '/') ?? "";
                var relsPath = $"{dir}/_rels/{entry.Name}.rels";
                var re = zip.GetEntry(relsPath);
                if (re == null) return d;
                using var rs = re.Open();
                var rd = XDocument.Load(rs);
                foreach (var rel in rd.Descendants(relsNs + "Relationship"))
                {
                    var id = rel.Attribute("Id")?.Value;
                    var tgt = rel.Attribute("Target")?.Value;
                    if (id != null && tgt != null) d[id] = tgt;
                }
                return d;
            }

            // ── Helper: parse background from a document's <p:bg> ─────────────────
            (string color, string image) ParseBackground(XDocument doc, Dictionary<string, string> rels)
            {
                var bgEl = doc.Descendants(pNs + "bg").FirstOrDefault();
                if (bgEl == null) return ("#ffffff", "");

                var bgPr = bgEl.Element(pNs + "bgPr");
                if (bgPr != null)
                {
                    // Solid fill
                    var solid = bgPr.Element(aNs + "solidFill");
                    if (solid != null) return (ResolveColor(solid, "#ffffff"), "");

                    // Gradient fill — use first stop color
                    var grad = bgPr.Descendants(aNs + "gs").FirstOrDefault();
                    if (grad != null) return (ResolveColor(grad.Element(aNs + "solidFill") ?? grad, "#ffffff"), "");

                    // Blip fill (image background)
                    var blip = bgPr.Element(aNs + "blipFill")?.Element(aNs + "blip")
                               ?? bgPr.Descendants(aNs + "blip").FirstOrDefault();
                    if (blip != null)
                    {
                        var embedId = blip.Attribute(rNs + "embed")?.Value;
                        if (embedId != null && rels.TryGetValue(embedId, out var tgt))
                        {
                            var url = ExtractImage(ResolveMediaPath(tgt));
                            if (url != null) return ("#ffffff", url);
                        }
                    }
                }

                // bgRef fallback — solid fill via scheme color
                var bgRef = bgEl.Element(pNs + "bgRef");
                if (bgRef != null) return (ResolveColor(bgRef, "#ffffff"), "");

                return ("#ffffff", "");
            }

            // ── Helper: font size from rPr sz attribute ────────────────────────────
            // sz in hundredths of a point. PPTX reference height in pt = slideEmuH / 914400 * 72.
            // We render to canvasH pixels, so scale = canvasH / (slideEmuH / 914400 * 72).
            double slidePtH = slideEmuH / 914400.0 * 72.0; // slide height in points
            double ParseFontSize(XElement? rPr, double defaultPt = 18)
            {
                var szAttr = rPr?.Attribute("sz")?.Value;
                if (szAttr == null || !double.TryParse(szAttr, out var sz)) return Math.Max(6, defaultPt * canvasH / slidePtH);
                double pt = sz / 100.0;
                return Math.Max(6, pt * canvasH / slidePtH);
            }

            // ── Helper: parse a single sp/pic element into fabric object ──────────
            // tx: group transform context — (offX, offY, scaleX, scaleY) in slide EMU space
            bool IsPlaceholder(XElement el)
            {
                return el.Element(pNs + "nvSpPr")?.Element(pNs + "nvPr")?.Element(pNs + "ph") != null;
            }

            // Group transform: maps child-space coords to slide-space coords
            static GrpTransform? ParseGrpTransform(XElement grpSp, XNamespace aNs)
            {
                var xfrm = grpSp.Element(aNs + "xfrm") // inside grpSpPr
                        ?? grpSp.Descendants(aNs + "xfrm").FirstOrDefault();
                if (xfrm == null) return null;
                var off = xfrm.Element(aNs + "off");
                var ext = xfrm.Element(aNs + "ext");
                var chOff = xfrm.Element(aNs + "chOff");
                var chExt = xfrm.Element(aNs + "chExt");
                if (off == null || ext == null || chOff == null || chExt == null) return null;

                double gOffX = double.TryParse(off.Attribute("x")?.Value, out var gox) ? gox : 0;
                double gOffY = double.TryParse(off.Attribute("y")?.Value, out var goy) ? goy : 0;
                double gExtCx = double.TryParse(ext.Attribute("cx")?.Value, out var gcx) ? gcx : 1;
                double gExtCy = double.TryParse(ext.Attribute("cy")?.Value, out var gcy) ? gcy : 1;
                double chOffX = double.TryParse(chOff.Attribute("x")?.Value, out var cox) ? cox : 0;
                double chOffY = double.TryParse(chOff.Attribute("y")?.Value, out var coy) ? coy : 0;
                double chExtCx = double.TryParse(chExt.Attribute("cx")?.Value, out var ccx) && ccx != 0 ? ccx : gExtCx;
                double chExtCy = double.TryParse(chExt.Attribute("cy")?.Value, out var ccy) && ccy != 0 ? ccy : gExtCy;

                // child -> slide: slideX = gOffX + (childX - chOffX) * (gExtCx / chExtCx)
                return new GrpTransform(
                    gOffX - chOffX * (gExtCx / chExtCx),
                    gOffY - chOffY * (gExtCy / chExtCy),
                    gExtCx / chExtCx,
                    gExtCy / chExtCy
                );
            }

            object? ParseElement(XElement el, Dictionary<string, string> rels, GrpTransform? grp = null)
            {
                // Apply group transform to convert child EMU coords to slide EMU coords
                double ApplyGrpX(double v) => grp == null ? v : grp.OffX + v * grp.ScX;
                double ApplyGrpY(double v) => grp == null ? v : grp.OffY + v * grp.ScY;
                double ApplyGrpCx(double v) => grp == null ? v : v * grp.ScX;
                double ApplyGrpCy(double v) => grp == null ? v : v * grp.ScY;
                if (el.Name == pNs + "sp")
                {
                    var spPr = el.Element(pNs + "spPr");
                    if (spPr == null) return null;

                    var xfrm = spPr.Element(aNs + "xfrm");
                    if (xfrm == null) return null;

                    var off = xfrm.Element(aNs + "off");
                    var ext = xfrm.Element(aNs + "ext");
                    if (off == null || ext == null) return null;

                    double rawX = double.TryParse(off.Attribute("x")?.Value, out var ox) ? ox : 0;
                    double rawY = double.TryParse(off.Attribute("y")?.Value, out var oy) ? oy : 0;
                    double rawCx = double.TryParse(ext.Attribute("cx")?.Value, out var cxv) ? cxv : 100000;
                    double rawCy = double.TryParse(ext.Attribute("cy")?.Value, out var cyv) ? cyv : 50000;

                    double ex = ApplyGrpX(rawX) * scaleX;
                    double ey = ApplyGrpY(rawY) * scaleY;
                    double ew = ApplyGrpCx(rawCx) * scaleX;
                    double eh = ApplyGrpCy(rawCy) * scaleY;
                    if (ew < 4) ew = 80;
                    if (eh < 4) eh = 20;

                    // Rotation in 60000ths of a degree
                    double angle = 0;
                    var rotAttr = xfrm.Attribute("rot")?.Value;
                    if (rotAttr != null && double.TryParse(rotAttr, out var rot)) angle = rot / 60000.0;

                    // Fill — check blipFill first (image fill), then solidFill
                    var blipFillInSp = spPr.Element(aNs + "blipFill");
                    string? blipImageUrl = null;
                    if (blipFillInSp != null)
                    {
                        var blipEl = blipFillInSp.Element(aNs + "blip");
                        var embedId = blipEl?.Attribute(rNs + "embed")?.Value;
                        if (embedId != null && rels.TryGetValue(embedId, out var blipTarget))
                            blipImageUrl = ExtractImage(ResolveMediaPath(blipTarget));
                    }
                    var fillColor = ResolveColor(spPr.Element(aNs + "solidFill") ?? spPr.Descendants(aNs + "solidFill").FirstOrDefault(), "transparent");
                    // noFill -> transparent
                    if (spPr.Element(aNs + "noFill") != null) fillColor = "transparent";

                    // Stroke
                    var strokeColor = "";
                    var ln = spPr.Element(aNs + "ln");
                    if (ln != null)
                    {
                        if (ln.Element(aNs + "noFill") != null) strokeColor = "";
                        else strokeColor = ResolveColor(ln.Element(aNs + "solidFill") ?? ln.Descendants(aNs + "solidFill").FirstOrDefault(), "");
                    }
                    double strokeWidth = 0;
                    if (!string.IsNullOrEmpty(strokeColor))
                    {
                        var wAttr = ln?.Attribute("w")?.Value;
                        if (wAttr != null && double.TryParse(wAttr, out var lw)) strokeWidth = Math.Max(0.5, lw / 12700.0 * Math.Min(scaleX, scaleY) * 100);
                        else strokeWidth = 1;
                    }

                    // Shape geometry
                    var prstGeom = spPr.Element(aNs + "prstGeom")?.Attribute("prst")?.Value ?? "";

                    var txBody = el.Element(pNs + "txBody");
                    if (txBody != null)
                    {
                        // ── Text box ──────────────────────────────────────────────
                        var lines = new System.Text.StringBuilder();
                        foreach (var para in txBody.Elements(aNs + "p"))
                        {
                            var paraText = new System.Text.StringBuilder();
                            foreach (var run in para.Elements(aNs + "r"))
                                paraText.Append(run.Element(aNs + "t")?.Value ?? "");
                            // handle field elements too
                            foreach (var fld in para.Elements(aNs + "fld"))
                                paraText.Append(fld.Element(aNs + "t")?.Value ?? "");
                            lines.Append(paraText.ToString());
                            lines.Append('\n');
                        }
                        var text = lines.ToString().TrimEnd('\n');
                        // Skip truly empty placeholder textboxes (no runs, only paragraph markers)
                        if (string.IsNullOrWhiteSpace(text)) return null;

                        // Get first run's rPr for formatting
                        var firstRPr = txBody.Descendants(aNs + "r").FirstOrDefault()?.Element(aNs + "rPr");
                        // Also check paragraph-level pPr defRPr
                        var defRPr = txBody.Descendants(aNs + "defRPr").FirstOrDefault();
                        var effectiveRPr = firstRPr ?? defRPr;

                        double fontSize = ParseFontSize(effectiveRPr, 18);

                        var textColor = "#1e293b";
                        var tcFill = effectiveRPr?.Element(aNs + "solidFill")
                                    ?? effectiveRPr?.Descendants(aNs + "solidFill").FirstOrDefault();
                        if (tcFill != null) textColor = ResolveColor(tcFill, "#1e293b");

                        var bold = effectiveRPr?.Attribute("b")?.Value == "1";
                        var italic = effectiveRPr?.Attribute("i")?.Value == "1";

                        // Text alignment from paragraph properties
                        var algn = txBody.Elements(aNs + "p").FirstOrDefault()?.Element(aNs + "pPr")?.Attribute("algn")?.Value ?? "l";
                        var textAlign = algn switch { "ctr" => "center", "r" => "right", "just" => "justify", _ => "left" };

                        var obj = new Dictionary<string, object>
                        {
                            ["type"] = "textbox",
                            ["version"] = "5.3.0",
                            ["left"] = Math.Round(ex, 1),
                            ["top"] = Math.Round(ey, 1),
                            ["width"] = Math.Round(ew, 1),
                            ["fontSize"] = Math.Round(fontSize, 1),
                            ["fontWeight"] = bold ? "bold" : "normal",
                            ["fontStyle"] = italic ? "italic" : "normal",
                            ["fill"] = textColor,
                            ["text"] = text,
                            ["fontFamily"] = "Inter",
                            ["textAlign"] = textAlign,
                            ["editable"] = true,
                            ["selectable"] = true,
                            ["id"] = Guid.NewGuid().ToString("N")[..8],
                            ["name"] = "Văn bản"
                        };
                        if (angle != 0) obj["angle"] = Math.Round(angle, 1);
                        if (!string.IsNullOrEmpty(strokeColor)) { obj["stroke"] = strokeColor; obj["strokeWidth"] = Math.Round(strokeWidth, 1); }
                        if (fillColor != "transparent") obj["backgroundColor"] = fillColor;
                        return obj;
                    }
                    else
                    {
                        // If shape has image fill, emit as image object
                        if (blipImageUrl != null)
                        {
                            var imgObj2 = new Dictionary<string, object>
                            {
                                ["type"] = "image",
                                ["version"] = "5.3.0",
                                ["left"] = Math.Round(ex, 1),
                                ["top"] = Math.Round(ey, 1),
                                ["width"] = Math.Round(ew, 1),
                                ["height"] = Math.Round(eh, 1),
                                ["scaleX"] = 1.0,
                                ["scaleY"] = 1.0,
                                ["src"] = blipImageUrl,
                                ["crossOrigin"] = "anonymous",
                                ["selectable"] = true,
                                ["id"] = Guid.NewGuid().ToString("N")[..8],
                                ["name"] = "Hình ảnh"
                            };
                            if (angle != 0) imgObj2["angle"] = Math.Round(angle, 1);
                            return imgObj2;
                        }

                        // ── Shape ─────────────────────────────────────────────────
                        bool isLine = prstGeom is "line" or "straightConnector1" or "bentConnector3" or "curvedConnector3";
                        string shapeType = prstGeom switch
                        {
                            "ellipse" or "circle" => "ellipse",
                            "roundRect" => "rect",
                            "triangle" => "triangle",
                            _ when isLine => "line",
                            _ => "rect"
                        };

                        if (isLine)
                        {
                            // fabric.Line: x1,y1,x2,y2 in object local space, positioned by left/top
                            var lineObj = new Dictionary<string, object>
                            {
                                ["type"] = "line",
                                ["version"] = "5.3.0",
                                ["left"] = Math.Round(ex, 1),
                                ["top"] = Math.Round(ey + eh / 2, 1),
                                ["x1"] = 0.0,
                                ["y1"] = 0.0,
                                ["x2"] = Math.Round(ew, 1),
                                ["y2"] = 0.0,
                                ["stroke"] = string.IsNullOrEmpty(strokeColor) ? fillColor : strokeColor,
                                ["strokeWidth"] = strokeWidth < 0.5 ? 2.0 : Math.Round(strokeWidth, 1),
                                ["fill"] = "transparent",
                                ["selectable"] = true,
                                ["id"] = Guid.NewGuid().ToString("N")[..8],
                                ["name"] = "Đường"
                            };
                            if (angle != 0) lineObj["angle"] = Math.Round(angle, 1);
                            return lineObj;
                        }

                        var obj = new Dictionary<string, object>
                        {
                            ["type"] = shapeType,
                            ["version"] = "5.3.0",
                            ["left"] = Math.Round(ex, 1),
                            ["top"] = Math.Round(ey, 1),
                            ["width"] = Math.Round(ew, 1),
                            ["height"] = Math.Round(eh, 1),
                            ["fill"] = fillColor,
                            ["selectable"] = true,
                            ["id"] = Guid.NewGuid().ToString("N")[..8],
                            ["name"] = "Hình"
                        };
                        if (angle != 0) obj["angle"] = Math.Round(angle, 1);
                        if (shapeType == "rect")
                        {
                            double rx = prstGeom == "roundRect" ? Math.Min(ew, eh) * 0.1 : 0;
                            obj["rx"] = Math.Round(rx, 1);
                            obj["ry"] = Math.Round(rx, 1);
                        }
                        if (shapeType == "ellipse") { obj["rx"] = Math.Round(ew / 2, 1); obj["ry"] = Math.Round(eh / 2, 1); }
                        if (!string.IsNullOrEmpty(strokeColor)) { obj["stroke"] = strokeColor; obj["strokeWidth"] = Math.Round(strokeWidth, 1); }
                        return obj;
                    }
                }
                else if (el.Name == pNs + "pic")
                {
                    var blipFill = el.Element(pNs + "blipFill");
                    var blip = blipFill?.Element(aNs + "blip");
                    var embedId = blip?.Attribute(rNs + "embed")?.Value;
                    if (string.IsNullOrEmpty(embedId)) return null;
                    if (!rels.TryGetValue(embedId, out var targetPath)) return null;

                    var url = ExtractImage(ResolveMediaPath(targetPath));
                    if (url == null) return null;

                    var spPr2 = el.Element(pNs + "spPr");
                    if (spPr2 == null) return null;
                    var xfrm2 = spPr2.Element(aNs + "xfrm");
                    if (xfrm2 == null) return null;
                    var off2 = xfrm2.Element(aNs + "off");
                    var ext2 = xfrm2.Element(aNs + "ext");
                    if (off2 == null || ext2 == null) return null;

                    double ex2 = ApplyGrpX(double.TryParse(off2.Attribute("x")?.Value, out var ox2) ? ox2 : 0) * scaleX;
                    double ey2 = ApplyGrpY(double.TryParse(off2.Attribute("y")?.Value, out var oy2) ? oy2 : 0) * scaleY;
                    double ew2 = ApplyGrpCx(double.TryParse(ext2.Attribute("cx")?.Value, out var cxv2) ? cxv2 : 100000) * scaleX;
                    double eh2 = ApplyGrpCy(double.TryParse(ext2.Attribute("cy")?.Value, out var cyv2) ? cyv2 : 100000) * scaleY;
                    if (ew2 < 4) ew2 = 80;
                    if (eh2 < 4) eh2 = 80;

                    double angle2 = 0;
                    var rotAttr2 = xfrm2.Attribute("rot")?.Value;
                    if (rotAttr2 != null && double.TryParse(rotAttr2, out var rot2)) angle2 = rot2 / 60000.0;

                    var imgObj = new Dictionary<string, object>
                    {
                        ["type"] = "image",
                        ["version"] = "5.3.0",
                        ["left"] = Math.Round(ex2, 1),
                        ["top"] = Math.Round(ey2, 1),
                        ["width"] = Math.Round(ew2, 1),
                        ["height"] = Math.Round(eh2, 1),
                        ["scaleX"] = 1.0,
                        ["scaleY"] = 1.0,
                        ["src"] = url,
                        ["crossOrigin"] = "anonymous",
                        ["selectable"] = true,
                        ["id"] = Guid.NewGuid().ToString("N")[..8],
                        ["name"] = "Hình ảnh"
                    };
                    if (angle2 != 0) imgObj["angle"] = Math.Round(angle2, 1);
                    return imgObj;
                }
                return null;
            }

            // ── Load slide master objects (ppt/slideMasters/slideMaster1.xml) ─────
            var masterObjects = new List<object>();
            var masterBgColor = "#ffffff";
            var masterBgImage = "";
            var masterEntry = zip.GetEntry("ppt/slideMasters/slideMaster1.xml");
            if (masterEntry != null)
            {
                using var ms2 = masterEntry.Open();
                var masterDoc = XDocument.Load(ms2);
                var masterRels = LoadRels(masterEntry);
                var (mc, mi) = ParseBackground(masterDoc, masterRels);
                masterBgColor = mc; masterBgImage = mi;
                var spTree = masterDoc.Descendants(pNs + "spTree").FirstOrDefault();
                    if (spTree != null)
                    {
                        void CollectMaster(XElement container, Dictionary<string, string> r, GrpTransform? grpCtx)
                        {
                            foreach (var el in container.Elements())
                            {
                                if ((el.Name == pNs + "sp" || el.Name == pNs + "pic") && !IsPlaceholder(el))
                                {
                                    var obj = ParseElement(el, r, grpCtx);
                                    if (obj != null) masterObjects.Add(obj);
                                }
                                else if (el.Name == pNs + "grpSp")
                                {
                                    var gt = ParseGrpTransform(el.Element(pNs + "grpSpPr") ?? el, aNs);
                                    CollectMaster(el, r, gt);
                                }
                            }
                        }
                        CollectMaster(spTree, masterRels, null);
                    }
            }

            // ── Load slide layouts (ppt/slideLayouts/slideLayoutN.xml) ────────────
            // Map layout path -> (bgColor, bgImage, objects)
            var layoutData = new Dictionary<string, (string color, string image, List<object> objs)>();
            foreach (var lEntry in zip.Entries.Where(e => e.FullName.StartsWith("ppt/slideLayouts/") && e.FullName.EndsWith(".xml") && !e.FullName.Contains("/_rels/")))
            {
                using var ls = lEntry.Open();
                var lDoc = XDocument.Load(ls);
                var lRels = LoadRels(lEntry);
                var (lc, li) = ParseBackground(lDoc, lRels);
                var lObjs = new List<object>();
                var spTree = lDoc.Descendants(pNs + "spTree").FirstOrDefault();
                if (spTree != null)
                {
                    void CollectLayout(XElement container, Dictionary<string, string> r, GrpTransform? grpCtx)
                    {
                        foreach (var el in container.Elements())
                        {
                            if ((el.Name == pNs + "sp" || el.Name == pNs + "pic") && !IsPlaceholder(el))
                            {
                                var obj = ParseElement(el, lRels, grpCtx);
                                if (obj != null) lObjs.Add(obj);
                            }
                            else if (el.Name == pNs + "grpSp")
                            {
                                var gt = ParseGrpTransform(el.Element(pNs + "grpSpPr") ?? el, aNs);
                                GrpTransform? composed = (gt, grpCtx) switch
                                {
                                    (null, _) => grpCtx,
                                    (_, null) => gt,
                                    _ => new GrpTransform(grpCtx.OffX + gt.OffX * grpCtx.ScX, grpCtx.OffY + gt.OffY * grpCtx.ScY, grpCtx.ScX * gt.ScX, grpCtx.ScY * gt.ScY)
                                };
                                CollectLayout(el, r, composed);
                            }
                        }
                    }
                    CollectLayout(spTree, lRels, null);
                }
                layoutData[lEntry.FullName] = (lc, li, lObjs);
            }

            // ── Process each slide ────────────────────────────────────────────────
            var slideEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml") && !e.FullName.Contains("/_rels/"))
                .OrderBy(e =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(e.Name, @"\d+");
                    return m.Success ? int.Parse(m.Value) : 0;
                })
                .ToList();

            int slideIdx = 0;
            foreach (var entry in slideEntries)
            {
                XDocument doc;
                using (var stream = entry.Open())
                    doc = XDocument.Load(stream);

                var rels = LoadRels(entry);

                // ── Background: use LibreOffice-rendered PNG if available ──────────
                // Otherwise fall back to parsed XML background color
                string bgImage = slideIdx < slideImageUrls.Count ? slideImageUrls[slideIdx] : "";
                string bgColor = "#ffffff";
                if (string.IsNullOrEmpty(bgImage))
                {
                    // Fallback: resolve layout chain for bg color
                    string slideLayoutPath = "";
                    foreach (var rel in rels)
                    {
                        if (rel.Value.Contains("slideLayout"))
                        {
                            slideLayoutPath = rel.Value.StartsWith("../") ? "ppt/" + rel.Value[3..] : rel.Value;
                            break;
                        }
                    }
                    layoutData.TryGetValue(slideLayoutPath, out var layoutInfo);
                    var (slideBgColor, slideBgImg) = ParseBackground(doc, rels);
                    bgColor = slideBgColor != "#ffffff" ? slideBgColor : layoutInfo.color ?? masterBgColor;
                    bgImage = !string.IsNullOrEmpty(slideBgImg) ? slideBgImg
                            : !string.IsNullOrEmpty(layoutInfo.image) ? layoutInfo.image
                            : masterBgImage;
                }

                var fabricObjects = new List<object>();
                fabricObjects.AddRange(masterObjects);

                string slideLayoutPathForObjects = "";
                foreach (var rel in rels)
                {
                    if (rel.Value.Contains("slideLayout"))
                    {
                        slideLayoutPathForObjects = rel.Value.StartsWith("../") ? "ppt/" + rel.Value[3..] : rel.Value;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(slideLayoutPathForObjects) && layoutData.TryGetValue(slideLayoutPathForObjects, out var editableLayoutInfo))
                    fabricObjects.AddRange(editableLayoutInfo.objs);

                var spTree = doc.Descendants(pNs + "spTree").FirstOrDefault();
                if (spTree != null)
                {
                    void CollectEditableElements(XElement container, GrpTransform? grpCtx)
                    {
                        foreach (var el in container.Elements())
                        {
                            if (el.Name == pNs + "sp" || el.Name == pNs + "pic")
                            {
                                var obj = ParseElement(el, rels, grpCtx);
                                if (obj != null) fabricObjects.Add(obj);
                            }
                            else if (el.Name == pNs + "grpSp")
                            {
                                var grpSpPr = el.Element(pNs + "grpSpPr");
                                var gt = grpSpPr != null ? ParseGrpTransform(grpSpPr, aNs) : null;
                                GrpTransform? childGrp = (gt, grpCtx) switch
                                {
                                    (null, _) => grpCtx,
                                    (_, null) => gt,
                                    _ => new GrpTransform(grpCtx.OffX + gt.OffX * grpCtx.ScX, grpCtx.OffY + gt.OffY * grpCtx.ScY, gt.ScX * grpCtx.ScX, gt.ScY * grpCtx.ScY)
                                };
                                CollectEditableElements(el, childGrp);
                            }
                        }
                    }
                    CollectEditableElements(spTree, null);
                }

                if (fabricObjects.Count > 0 && slideIdx < slideImageUrls.Count)
                    bgImage = "";
                var fabricJson = JsonSerializer.Serialize(new
                {
                    version = "5.3.0",
                    canvasWidth = canvasW,
                    canvasHeight = canvasH,
                    objects = fabricObjects
                });
                result.Add((fabricJson, bgColor, bgImage, slideIdx));
                slideIdx++;
            }

            return result;
        }

        private static string PrstColorToHex(string prst) => prst switch
        {
            "black" => "#000000", "white" => "#ffffff", "red" => "#ff0000",
            "green" => "#008000", "blue" => "#0000ff", "yellow" => "#ffff00",
            "cyan" => "#00ffff", "magenta" => "#ff00ff", "orange" => "#ffa500",
            "purple" => "#800080", "gray" or "grey" => "#808080",
            "darkBlue" => "#00008b", "darkGreen" => "#006400", "darkRed" => "#8b0000",
            "lightBlue" => "#add8e6", "lightGray" or "lightGrey" => "#d3d3d3",
            _ => "#cccccc"
        };
    }
}
