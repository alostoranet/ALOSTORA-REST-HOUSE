using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class CustomerMessageServiceTests : IDisposable
{
    private readonly string _root;
    private readonly CustomerMessageService _service;

    public CustomerMessageServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-messages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new CustomerMessageService(Path.Combine(_root, "messages.db"));
    }

    [Fact]
    public void AdministrationReplyIsReturnedOnlyToTheOriginalBrowserViewer()
    {
        var id = _service.Create(new CreateCustomerMessageRequest(
            "اقتراح", "طلب العميل", "أرجو إضافة هذا المحتوى.", null, "viewer-token-123"));

        Assert.Null(_service.GetForViewer(id, "another-viewer"));
        Assert.Null(_service.GetForViewer(id, ""));

        Assert.True(_service.SetAdminReply(id, "تم تنفيذ طلبك، شكراً لك."));

        var customerView = _service.GetForViewer(id, "viewer-token-123");
        Assert.NotNull(customerView);
        Assert.Equal(id, customerView!.Id);
        Assert.Equal("تم تنفيذ طلبك، شكراً لك.", customerView.AdminReply);
        Assert.False(string.IsNullOrWhiteSpace(customerView.AdminReplyUtc));

        var adminView = Assert.Single(_service.List());
        Assert.Equal("تم تنفيذ طلبك، شكراً لك.", adminView.AdminReply);
    }

    [Fact]
    public void AdministrationReplyRejectsEmptyOrOverlongText()
    {
        var id = _service.Create(new CreateCustomerMessageRequest(
            "أخرى", "عنوان الرسالة", "تفاصيل كافية للرسالة.", null, "viewer-token-456"));

        Assert.Throws<ArgumentException>(() => _service.SetAdminReply(id, " "));
        Assert.Throws<ArgumentException>(() => _service.SetAdminReply(id, new string('x', 2501)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
