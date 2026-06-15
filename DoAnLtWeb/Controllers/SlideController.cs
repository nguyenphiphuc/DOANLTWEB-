using Microsoft.AspNetCore.Mvc;
using DoAnLtWeb.Data;
using DoAnLtWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace DoAnLtWeb.Controllers
{
    [Authorize]
    public class SlideController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IWebHostEnvironment _env;

        public SlideController(AppDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var presentation = new Presentation
            {
                Title = "Ban thuyet trinh chua co ten",
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            presentation.Slides.Add(new Slide { PageNumber = 1, BackgroundColor = "#ffffff", ElementsJson = "[]" });
            _context.Presentations.Add(presentation);
            await _context.SaveChangesAsync();

            return RedirectToAction("Edit", new { id = presentation.Id });
        }

        [HttpGet]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CloneTemplate(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            var template = await _context.Presentations
                .Include(p => p.Slides)
                .FirstOrDefaultAsync(p => p.Id == id && p.IsTemplate);

            if (template == null) return NotFound();
            if (template.IsPremiumTemplate && !IsVip(user))
            {
                TempData["BillingError"] = "Template nay danh cho thanh vien VIP.";
                return RedirectToAction("Upgrade", "Billing");
            }

            var presentation = new Presentation
            {
                Title = template.Title + " (Ban sao)",
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            foreach (var slide in template.Slides.OrderBy(s => s.PageNumber))
            {
                presentation.Slides.Add(new Slide
                {
                    PageNumber = slide.PageNumber,
                    BackgroundColor = slide.BackgroundColor,
                    BackgroundImage = slide.BackgroundImage,
                    ElementsJson = slide.ElementsJson
                });
            }

            _context.Presentations.Add(presentation);
            await _context.SaveChangesAsync();

            return RedirectToAction("Edit", new { id = presentation.Id });
        }

        [HttpGet]
        public async Task<IActionResult> GetTemplates()
        {
            var userId = GetCurrentUserId();
            var user = userId == null ? null : await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);

            var trashKeywords = new[] { "China", "CQU", "Telecom", "CMB", "Chongqing" };
            var templatesList = await _context.Presentations
                .Include(p => p.Slides)
                .Where(p => p.IsTemplate && p.Slides.Any())
                .ToListAsync();

            var filteredTemplates = templatesList
                .Where(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Title, @"\[\d+")
                         && !trashKeywords.Any(kw => p.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => p.Category != null && p.Category.StartsWith("Slidify Mẫu /"))
                .ThenBy(p => p.Title)
                .Select(p => new
                {
                    p.Id,
                    p.Title,
                    p.Category,
                    p.ThumbnailUrl,
                    p.IsPremiumTemplate,
                    p.PremiumReason,
                    SlideCount = p.Slides.Count,
                    CanUse = !p.IsPremiumTemplate || (user != null && user.IsVip && (!user.VipExpiresAt.HasValue || user.VipExpiresAt > DateTime.UtcNow)),
                    Slides = p.Slides.OrderBy(s => s.PageNumber).Select(s => new {
                        s.BackgroundColor,
                        s.BackgroundImage,
                        s.ElementsJson
                    }).ToList()
                })
                .ToList();

            return Json(filteredTemplates);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            // Owner or anyone admitted through a share link can open the editor.
            var presentation = await _context.Presentations
                .Include(p => p.Slides.OrderBy(s => s.PageNumber))
                .FirstOrDefaultAsync(p => p.Id == id);
            if (presentation == null) return NotFound();

            bool isOwner = presentation.UserId == userId.Value;
            SharePermission? sharedPerm = null;
            int? shareOwnerId = null;
            if (!isOwner)
            {
                var membership = await (from m in _context.PresentationShareMembers
                                        join s in _context.PresentationShares on m.ShareId equals s.Id
                                        where s.PresentationId == id && m.UserId == userId.Value && s.IsActive
                                        select new { m.Permission, s.OwnerUserId })
                                       .FirstOrDefaultAsync();
                if (membership == null) return NotFound();
                sharedPerm = membership.Permission;
                shareOwnerId = membership.OwnerUserId;
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            ViewBag.IsVip = IsVip(user);
            ViewBag.IsOwner = isOwner;
            ViewBag.SharedPermission = sharedPerm?.ToString();
            ViewBag.ShareOwnerId = shareOwnerId;
            return View(presentation);
        }

        // ───── Sharing / collaborative editing ─────────────────────────────

        [HttpPost]
        public async Task<IActionResult> CreateShare(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (!IsVip(user))
            {
                return Json(new { ok = false, reason = "vip-required",
                    message = "Tính năng chia sẻ chỉ dành cho thành viên VIP. Người được mời không cần VIP." });
            }

            var pres = await _context.Presentations.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId.Value);
            if (pres == null) return NotFound();

            var share = await _context.PresentationShares.FirstOrDefaultAsync(s => s.PresentationId == id);
            if (share == null)
            {
                share = new PresentationShare
                {
                    PresentationId = id,
                    OwnerUserId = userId.Value,
                    ShareToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8),
                    DefaultPermission = SharePermission.Editor,
                    IsActive = true
                };
                _context.PresentationShares.Add(share);
                await _context.SaveChangesAsync();
            }
            else if (!share.IsActive)
            {
                share.IsActive = true;
                await _context.SaveChangesAsync();
            }

            var origin = $"{Request.Scheme}://{Request.Host}";
            return Json(new { ok = true, token = share.ShareToken, url = $"{origin}/Slide/Join/{share.ShareToken}" });
        }

        [HttpGet]
        public async Task<IActionResult> Join(string token)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var share = await _context.PresentationShares
                .Include(s => s.Presentation)
                .Include(s => s.OwnerUser)
                .FirstOrDefaultAsync(s => s.ShareToken == token);
            if (share == null || share.Presentation == null) return NotFound();

            // Owner -> jump straight to their editor, no confirmation page.
            if (share.OwnerUserId == userId.Value) return RedirectToAction("Edit", new { id = share.PresentationId });

            if (!share.IsActive)
            {
                ViewBag.JoinError = "Liên kết chia sẻ này đã bị tắt.";
                ViewBag.ShareToken = token;
                ViewBag.OwnerEmail = share.OwnerUser?.Email ?? "?";
                ViewBag.PresentationTitle = share.Presentation.Title;
                return View("JoinShare", share.Presentation);
            }

            // Show a confirmation page so the user knows what they're joining instead of dropping
            // them straight into the editor. They click "Bắt đầu chỉnh sửa" to actually join.
            ViewBag.ShareToken = token;
            ViewBag.OwnerEmail = share.OwnerUser?.Email ?? "?";
            ViewBag.PresentationTitle = share.Presentation.Title;
            ViewBag.SlideCount = await _context.Slides.CountAsync(s => s.PresentationId == share.PresentationId);
            ViewBag.AlreadyMember = await _context.PresentationShareMembers
                .AnyAsync(m => m.ShareId == share.Id && m.UserId == userId.Value);
            return View("JoinShare", share.Presentation);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmJoin(string token)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var share = await _context.PresentationShares
                .FirstOrDefaultAsync(s => s.ShareToken == token);
            if (share == null) return NotFound();
            if (share.OwnerUserId == userId.Value) return RedirectToAction("Edit", new { id = share.PresentationId });
            if (!share.IsActive)
            {
                TempData["BillingError"] = "Liên kết chia sẻ đã bị tắt.";
                return RedirectToAction("Index", "Home");
            }

            var existing = await _context.PresentationShareMembers
                .FirstOrDefaultAsync(m => m.ShareId == share.Id && m.UserId == userId.Value);
            if (existing == null)
            {
                _context.PresentationShareMembers.Add(new PresentationShareMember
                {
                    ShareId = share.Id,
                    UserId = userId.Value,
                    Permission = share.DefaultPermission,
                    JoinedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Edit", new { id = share.PresentationId });
        }

        [HttpGet]
        public async Task<IActionResult> ShareMembers(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var share = await _context.PresentationShares
                .FirstOrDefaultAsync(s => s.PresentationId == id && s.OwnerUserId == userId.Value);
            if (share == null) return Json(new { ok = true, members = Array.Empty<object>(), share = (object?)null });

            var members = await _context.PresentationShareMembers
                .Include(m => m.User)
                .Where(m => m.ShareId == share.Id)
                .OrderBy(m => m.JoinedAt)
                .Select(m => new
                {
                    m.Id,
                    m.UserId,
                    email = m.User != null ? m.User.Email : "?",
                    permission = m.Permission.ToString(),
                    joinedAt = m.JoinedAt
                })
                .ToListAsync();
            var origin = $"{Request.Scheme}://{Request.Host}";
            return Json(new
            {
                ok = true,
                share = new
                {
                    share.Id,
                    token = share.ShareToken,
                    url = $"{origin}/Slide/Join/{share.ShareToken}",
                    isActive = share.IsActive,
                    defaultPermission = share.DefaultPermission.ToString()
                },
                members
            });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveShareMember(int id, int memberId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var share = await _context.PresentationShares
                .FirstOrDefaultAsync(s => s.PresentationId == id && s.OwnerUserId == userId.Value);
            if (share == null) return Forbid();

            var m = await _context.PresentationShareMembers.FirstOrDefaultAsync(x => x.Id == memberId && x.ShareId == share.Id);
            if (m == null) return NotFound();

            _context.PresentationShareMembers.Remove(m);
            await _context.SaveChangesAsync();
            return Ok(new { ok = true });
        }

        [HttpPost]
        public async Task<IActionResult> SetShareMemberPermission(int id, int memberId, string permission)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var share = await _context.PresentationShares
                .FirstOrDefaultAsync(s => s.PresentationId == id && s.OwnerUserId == userId.Value);
            if (share == null) return Forbid();

            var m = await _context.PresentationShareMembers.FirstOrDefaultAsync(x => x.Id == memberId && x.ShareId == share.Id);
            if (m == null) return NotFound();

            if (Enum.TryParse<SharePermission>(permission, true, out var p)) m.Permission = p;
            await _context.SaveChangesAsync();
            return Ok(new { ok = true });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleShare(int id, bool active)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var share = await _context.PresentationShares
                .FirstOrDefaultAsync(s => s.PresentationId == id && s.OwnerUserId == userId.Value);
            if (share == null) return NotFound();
            share.IsActive = active;
            await _context.SaveChangesAsync();
            return Ok(new { ok = true });
        }

        [HttpGet]
        public async Task<IActionResult> SyncCheck(int id)
        {
            // Lightweight endpoint the editor polls every few seconds to detect external edits.
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var pres = await _context.Presentations
                .Where(p => p.Id == id)
                .Select(p => new { p.Id, p.UpdatedAt })
                .FirstOrDefaultAsync();
            if (pres == null) return NotFound();
            return Json(new { ok = true, updatedAt = pres.UpdatedAt });
        }

        [HttpPost]
        public async Task<IActionResult> SavePresentation(int id, [FromBody] PresentationSaveDto data)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            // Owner or an Editor-level share member can save. Viewers get 403.
            var presentation = await _context.Presentations
                .Include(p => p.Slides)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (presentation == null) return NotFound();
            if (presentation.UserId != userId.Value)
            {
                var canEdit = await (from m in _context.PresentationShareMembers
                                     join s in _context.PresentationShares on m.ShareId equals s.Id
                                     where s.PresentationId == id && m.UserId == userId.Value && s.IsActive
                                       && m.Permission == SharePermission.Editor
                                     select m.Id).AnyAsync();
                if (!canEdit) return Forbid();
            }

            if (!string.IsNullOrEmpty(data.Title))
            {
                presentation.Title = data.Title;
            }

            if (!string.IsNullOrEmpty(data.ThumbnailUrl))
            {
                if (data.ThumbnailUrl.StartsWith("data:image"))
                {
                    try
                    {
                        var base64Data = data.ThumbnailUrl.Split(',')[1];
                        var bytes = Convert.FromBase64String(base64Data);
                        string uploadsFolder = Path.Combine(_env.WebRootPath, "thumbnails");
                        if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
                        string fileName = $"thumb_{presentation.Id}.jpg";
                        string filePath = Path.Combine(uploadsFolder, fileName);
                        await System.IO.File.WriteAllBytesAsync(filePath, bytes);
                        presentation.ThumbnailUrl = $"/thumbnails/{fileName}";
                    }
                    catch
                    {
                        presentation.ThumbnailUrl = data.ThumbnailUrl;
                    }
                }
                else
                {
                    presentation.ThumbnailUrl = data.ThumbnailUrl;
                }
            }

            var existingSlides = presentation.Slides.OrderBy(s => s.PageNumber).ToList();
            int pageNumber = 1;

            for (int i = 0; i < data.Slides.Count; i++)
            {
                var slideDto = data.Slides[i];
                if (i < existingSlides.Count)
                {
                    var existingSlide = existingSlides[i];
                    existingSlide.PageNumber = pageNumber++;
                    existingSlide.BackgroundColor = string.IsNullOrWhiteSpace(slideDto.BackgroundColor) ? "#ffffff" : slideDto.BackgroundColor;
                    existingSlide.BackgroundImage = slideDto.BackgroundImage;
                    existingSlide.ElementsJson = string.IsNullOrWhiteSpace(slideDto.ElementsJson) ? "[]" : slideDto.ElementsJson;
                }
                else
                {
                    presentation.Slides.Add(new Slide
                    {
                        PageNumber = pageNumber++,
                        BackgroundColor = string.IsNullOrWhiteSpace(slideDto.BackgroundColor) ? "#ffffff" : slideDto.BackgroundColor,
                        BackgroundImage = slideDto.BackgroundImage,
                        ElementsJson = string.IsNullOrWhiteSpace(slideDto.ElementsJson) ? "[]" : slideDto.ElementsJson
                    });
                }
            }

            if (existingSlides.Count > data.Slides.Count)
            {
                var slidesToRemove = existingSlides.Skip(data.Slides.Count).ToList();
                _context.Slides.RemoveRange(slidesToRemove);
                foreach (var s in slidesToRemove)
                {
                    presentation.Slides.Remove(s);
                }
            }

            presentation.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> UploadImage(IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest("Vui long chon anh hop le.");

            string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            string uniqueFileName = Guid.NewGuid() + "_" + Path.GetFileName(file.FileName);
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }

            return Ok(new { url = "/uploads/" + uniqueFileName });
        }

        [HttpPost]
        public async Task<IActionResult> SubmitAsTemplate(int id, [FromBody] TemplateSubmissionRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var pres = await _context.Presentations
                .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId.Value);
            if (pres == null) return NotFound();

            // Reject if a pending submission already exists for this deck — avoid duplicate spam.
            bool exists = await _context.TemplateSubmissions.AnyAsync(s =>
                s.PresentationId == id && s.Status == Models.TemplateSubmissionStatus.Pending);
            if (exists) return Ok(new { success = true, alreadyPending = true });

            var submission = new Models.TemplateSubmission
            {
                UserId = userId.Value,
                PresentationId = id,
                ProposedTitle = string.IsNullOrWhiteSpace(req?.Title) ? pres.Title : req.Title.Trim(),
                ProposedCategory = string.IsNullOrWhiteSpace(req?.Category) ? "Đa dụng" : req.Category.Trim(),
                Note = req?.Note?.Trim() ?? string.Empty,
                Status = Models.TemplateSubmissionStatus.Pending,
                SubmittedAt = DateTime.UtcNow
            };
            _context.TemplateSubmissions.Add(submission);
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        public class TemplateSubmissionRequest
        {
            public string Title { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string Note { get; set; } = string.Empty;
        }

        [HttpPost]
        public async Task<IActionResult> PublishAsTemplate(int id)
        {
            var isAdmin = User.IsInRole("Admin") || User.Identity?.Name == "admin@gmail.com";
            if (!isAdmin) return Unauthorized("Chi Admin moi co the dang Mau");

            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var presentation = await _context.Presentations
                .Include(p => p.Slides)
                .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId.Value);

            if (presentation == null) return NotFound();

            presentation.IsTemplate = true;
            presentation.IsPremiumTemplate = presentation.Slides.Count > 10 || presentation.Category.Contains("Premium", StringComparison.OrdinalIgnoreCase);
            presentation.PremiumReason = presentation.IsPremiumTemplate ? "Mau premium hoac bo slide dai tren 10 trang." : string.Empty;
            if (string.IsNullOrEmpty(presentation.Category))
            {
                presentation.Category = "Doanh nghiep";
            }

            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            // Soft delete: move to trash. Hard removal happens from /Home/Trash or automatically
            // after 30 days when the trash list is loaded.
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var presentation = await _context.Presentations.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId.Value);
            if (presentation == null) return NotFound();

            presentation.IsTrashed = true;
            presentation.TrashedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, trashed = true });
        }

        [HttpPost]
        public async Task<IActionResult> RestoreFromTrash(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var pres = await _context.Presentations.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId.Value);
            if (pres == null) return NotFound();
            pres.IsTrashed = false;
            pres.TrashedAt = null;
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> PurgeFromTrash(int id)
        {
            // Hard delete — only allowed on items already in the trash so a stray POST doesn't
            // nuke an active project.
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var pres = await _context.Presentations.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId.Value && p.IsTrashed);
            if (pres == null) return NotFound();
            _context.Presentations.Remove(pres);
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTemplate(int id)
        {
            if (!(User.IsInRole("Admin") || User.Identity?.Name == "admin@gmail.com"))
            {
                return Forbid();
            }

            var template = await _context.Presentations.FirstOrDefaultAsync(p => p.Id == id && p.IsTemplate);
            if (template == null) return NotFound();

            _context.Presentations.Remove(template);
            await _context.SaveChangesAsync();
            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        public async Task<IActionResult> Duplicate(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var presentation = await _context.Presentations
                .Include(p => p.Slides)
                .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId.Value);

            if (presentation == null) return NotFound();

            var newPresentation = new Presentation
            {
                Title = presentation.Title + " (Ban sao)",
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ThumbnailUrl = presentation.ThumbnailUrl,
                IsTemplate = false
            };

            foreach (var slide in presentation.Slides.OrderBy(s => s.PageNumber))
            {
                newPresentation.Slides.Add(new Slide
                {
                    PageNumber = slide.PageNumber,
                    BackgroundColor = slide.BackgroundColor,
                    BackgroundImage = slide.BackgroundImage,
                    ElementsJson = slide.ElementsJson
                });
            }

            _context.Presentations.Add(newPresentation);
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> CreateFromImport([FromBody] PresentationSaveDto data)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var presentation = new Presentation
            {
                Title = string.IsNullOrEmpty(data.Title) ? "Ban thuyet trinh nhap tu PDF" : data.Title,
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            int pageNum = 1;
            foreach (var slideDto in data.Slides)
            {
                presentation.Slides.Add(new Slide
                {
                    PageNumber = pageNum++,
                    BackgroundColor = string.IsNullOrEmpty(slideDto.BackgroundColor) ? "#ffffff" : slideDto.BackgroundColor,
                    BackgroundImage = slideDto.BackgroundImage,
                    ElementsJson = string.IsNullOrEmpty(slideDto.ElementsJson) ? "[]" : slideDto.ElementsJson
                });
            }

            _context.Presentations.Add(presentation);
            await _context.SaveChangesAsync();
            return Ok(new { id = presentation.Id });
        }

        private int? GetCurrentUserId()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out int userId) ? userId : null;
        }

        private static bool IsVip(User? user)
        {
            return user != null && user.IsVip && (!user.VipExpiresAt.HasValue || user.VipExpiresAt > DateTime.UtcNow);
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> SaveSeededTemplates([FromBody] TemplateImportDto data)
        {
            if (data == null || data.Templates == null) return BadRequest("Dữ liệu không hợp lệ.");

            var adminUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == "admin@gmail.com");
            if (adminUser == null) return BadRequest("Cần tạo tài khoản admin trước.");

            int count = 0;
            foreach (var tplDto in data.Templates)
            {
                // Delete existing template of the same title first to support clean upserting
                var existing = await _context.Presentations
                    .Include(p => p.Slides)
                    .FirstOrDefaultAsync(p => p.IsTemplate && p.Title == tplDto.Title);
                if (existing != null)
                {
                    _context.Presentations.Remove(existing);
                    await _context.SaveChangesAsync();
                }

                var presentation = new Presentation
                {
                    Title = tplDto.Title,
                    UserId = adminUser.Id,
                    IsTemplate = true,
                    IsPremiumTemplate = tplDto.Slides.Count > 10 || tplDto.Category.Contains("Premium", StringComparison.OrdinalIgnoreCase),
                    PremiumReason = tplDto.Slides.Count > 10 ? "Mẫu slide dài trên 10 trang." : string.Empty,
                    Category = tplDto.Category,
                    ThumbnailUrl = tplDto.ThumbnailUrl,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                foreach (var slideDto in tplDto.Slides)
                {
                    presentation.Slides.Add(new Slide
                    {
                        PageNumber = slideDto.PageNumber,
                        BackgroundColor = string.IsNullOrEmpty(slideDto.BackgroundColor) ? "#ffffff" : slideDto.BackgroundColor,
                        BackgroundImage = slideDto.BackgroundImage,
                        ElementsJson = string.IsNullOrEmpty(slideDto.ElementsJson) ? "[]" : slideDto.ElementsJson
                    });
                }

                _context.Presentations.Add(presentation);
                count++;
            }

            await _context.SaveChangesAsync();

            // Write payload to file for future automatic seeding
            try
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "generated-templates-editable.json");
                List<TemplateDto> allTemplates = new();
                if (System.IO.File.Exists(filePath))
                {
                    var existingJson = await System.IO.File.ReadAllTextAsync(filePath);
                    var existingDto = System.Text.Json.JsonSerializer.Deserialize<TemplateImportDto>(existingJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (existingDto != null && existingDto.Templates != null)
                    {
                        allTemplates = existingDto.Templates;
                    }
                }
                
                foreach (var newTpl in data.Templates)
                {
                    allTemplates.RemoveAll(t => string.Equals(t.Title, newTpl.Title, StringComparison.OrdinalIgnoreCase));
                    allTemplates.Add(newTpl);
                }
                
                var newDto = new TemplateImportDto { Templates = allTemplates };
                var serializeOptions = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var newJson = System.Text.Json.JsonSerializer.Serialize(newDto, serializeOptions);
                await System.IO.File.WriteAllTextAsync(filePath, newJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error saving generated-templates-editable.json: " + ex.Message);
            }

            return Ok(new { count = count });
        }

        [HttpPost]
        public async Task<IActionResult> ConvertToMarkdown(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Vui lòng chọn tệp hợp lệ.");

            // Create temporary uploads folder inside the workspace
            string tempFolder = Path.Combine(_env.WebRootPath, "uploads", "temp");
            if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

            string tempFileName = Guid.NewGuid() + Path.GetExtension(file.FileName);
            string tempFilePath = Path.Combine(tempFolder, tempFileName);

            try
            {
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                string scriptPath = Path.Combine(_env.ContentRootPath, "MarkItDownTool", "convert_to_md.py");
                if (!System.IO.File.Exists(scriptPath))
                {
                    return BadRequest($"Không tìm thấy script chuyển đổi tại {scriptPath}");
                }

                // Try executing python
                var (success, output, error) = ExecutePythonScript(scriptPath, tempFilePath);
                if (!success)
                {
                    return BadRequest($"Lỗi chuyển đổi: {error}");
                }

                return Ok(new { markdown = output });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Lỗi hệ thống: {ex.Message}");
            }
            finally
            {
                // Clean up the temp file
                try
                {
                    if (System.IO.File.Exists(tempFilePath))
                        System.IO.File.Delete(tempFilePath);
                }
                catch { /* ignore cleanup error */ }
            }
        }

        private (bool success, string output, string error) ExecutePythonScript(string scriptPath, string arg)
        {
            // We try "python", then "python3", then "py"
            var executables = new[] { "python", "python3", "py" };
            string lastError = "";

            foreach (var exe in executables)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = $"\"{scriptPath}\" \"{arg}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };

                    using var process = System.Diagnostics.Process.Start(psi);
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit(30000); // 30s timeout

                        if (process.ExitCode == 0)
                        {
                            return (true, output, "");
                        }
                        else
                        {
                            lastError = !string.IsNullOrEmpty(error) ? error : $"Exit code {process.ExitCode}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            return (false, "", $"Không thể thực thi Python. Đảm bảo Python đã được cài đặt và thêm vào PATH. Chi tiết lỗi: {lastError}");
        }
    }

    public class TemplateImportDto
    {
        public List<TemplateDto> Templates { get; set; } = new();
    }

    public class TemplateDto
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public List<SlideDto> Slides { get; set; } = new();
    }

    public class PresentationSaveDto
    {
        public string Title { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public List<SlideDto> Slides { get; set; } = new();
    }

    public class SlideDto
    {
        public int PageNumber { get; set; }
        public string BackgroundColor { get; set; } = string.Empty;
        public string BackgroundImage { get; set; } = string.Empty;
        public string ElementsJson { get; set; } = string.Empty;
    }
}
