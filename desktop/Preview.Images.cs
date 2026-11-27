using System.Net.Http.Headers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Views;
using SkiaSharp;

namespace MessengerDesktop;

public static partial class Preview
{
    static async Task AuditImages(Window window, Navigator nav, MessengerClient api, ProfileDto me)
    {
        var chatId = (await api.GetChats())[0].Id;
        var messageId = await api.SendMessage(chatId, "Картинки: пейзаж, портрет и недоступный файл");
        using var upload = new HttpClient { BaseAddress = new Uri(api.ServerBase + "/") };
        upload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
        async Task Attach(string name, byte[] bytes, string type)
        {
            using var content = new MultipartFormDataContent();
            using var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(type);
            content.Add(file, "file", name);
            using var response = await upload.PostAsync($"messages/{messageId}/attachments", content);
            response.EnsureSuccessStatusCode();
        }
        static byte[] Picture(int width, int height, SKEncodedImageFormat format)
        {
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.SteelBlue);
            using var paint = new SKPaint { Color = SKColors.Gold, IsAntialias = true };
            canvas.DrawCircle(width * .5f, height * .5f, Math.Min(width, height) * .3f, paint);
            using var encoded = bitmap.Encode(format, 90);
            return encoded.ToArray();
        }
        var landscape = Picture(1600, 800, SKEncodedImageFormat.Png);
        await Attach("Пейзаж.PNG", landscape, "application/octet-stream");
        await Attach("Портрет.jpg", Picture(600, 1200, SKEncodedImageFormat.Jpeg), "image/jpeg");
        await Attach("Повреждённая.png", [1, 2, 3], "image/png");
        await Attach("Документ.txt", [65], "text/plain");
        var dto = (await api.GetMessages(chatId)).Single(m => m.Id == messageId);
        Expect(dto.Attachments?.Count == 4, "Image attachments were not read from the API");
        dto.ContentState = "deleted";
        Expect(!MessageMapper.ToItem(dto, me.Id, _ => "").HasImages, "Deleted message exposes images");

        var vm = new ShellViewModel(nav, api, me);
        nav.Go(vm);
        async Task Until(Func<bool> condition)
        {
            for (var i = 0; i < 100; i++)
            {
                if (await Dispatcher.UIThread.InvokeAsync(condition)) return;
                await Task.Delay(50);
            }
            throw new Exception("Image check timed out");
        }
        await Until(() => vm.Chats.Count > 0);
        vm.SelectedChat = vm.Chats.Single(c => c.Id == chatId);
        await Until(() => vm.Messages.Any(m => m.Id == messageId));
        var message = vm.Messages.Single(m => m.Id == messageId);
        Expect(message.Images.Count == 3 && vm.ImagePreviews.Count == 0, "Non-images were included or loaded before opening");
        await Task.Delay(150);
        var shell = window.GetVisualDescendants().OfType<ShellView>().Single();
        MessageImagePreview[] Thumbnails() => shell.GetVisualDescendants().OfType<MessageImagePreview>()
            .Where(b => b.DataContext is AttachmentDto a && a.MessageId == messageId).ToArray();
        await Until(() => Thumbnails().Length == 3 && Thumbnails().Take(2).All(b =>
            b.GetVisualDescendants().OfType<Image>().Any(i => i.Source != null)));
        Expect(Thumbnails()[2].GetVisualDescendants().OfType<TextBlock>().Any(t =>
            t.Text?.Contains("недоступна") == true), "Inline corrupt image has no error");
        foreach (var width in new[] { 1180, 360 })
        {
            Resize(window, width);
            await Task.Delay(150);
            foreach (var thumbnail in Thumbnails())
                Expect(thumbnail.Bounds.Width <= 320 && thumbnail.Bounds.Height <= 288,
                    "Thumbnail exceeds album bounds");
            var album = shell.GetVisualDescendants().OfType<MessageImagePanel>().Single(p => p.Children.Count == 3);
            Expect(album.Bounds.Height <= 288 && album.Children[0].Bounds.Width > album.Children[1].Bounds.Width &&
                album.Children[1].Bounds.Top == album.Children[2].Bounds.Top,
                "Three-image album is not a compact mosaic");
            await Shot(window, $"images-inline-{width}");
        }
        var button = Thumbnails()[0];
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Expect(vm.Overlay == "images", "Thumbnail click did not open image sheet");
        Expect(vm.ImagePreviews.All(i => i.Status == "Загрузка картинки…"), "Missing loading state");
        await Until(() => vm.ImagePreviews.Count == 3 && vm.ImagePreviews.All(i => i.Status != "Загрузка картинки…"));
        Expect(vm.ImagePreviews.Take(2).All(i => i.Bitmap != null) && vm.ImagePreviews[2].Bitmap == null &&
            vm.ImagePreviews[2].Status?.Contains("Не удалось") == true, "Image decoding/error isolation failed");
        Expect(vm.ImagePreviews[0].Bitmap!.PixelSize == new PixelSize(1200, 600), "Landscape was not downsampled proportionally");

        foreach (var width in new[] { 1180, 360 })
        {
            Resize(window, width);
            await Task.Delay(150);
            var sheet = shell.FindControl<Border>("OverlaySheet")!;
            Expect(sheet.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Bottom &&
                sheet.Bounds.Width <= shell.Bounds.Width, "Image sheet does not fit at the bottom");
            foreach (var image in sheet.GetVisualDescendants().OfType<Image>().Where(i => i.Source != null))
            {
                Expect(image.Bounds.Width <= sheet.Bounds.Width && image.Bounds.Height <= 360,
                    "Image exceeds sheet bounds");
            }
            var scroll = shell.FindControl<ScrollViewer>("OverlayScroll")!;
            Expect(scroll.Extent.Height > scroll.Viewport.Height, "Multiple images cannot be scrolled");
            scroll.ScrollToHome();
            await Task.Delay(100);
            await Shot(window, $"images-{width}");
            scroll.ScrollToEnd();
            await Task.Delay(100);
            await Shot(window, $"images-error-{width}");
        }

        await Attach("Новая.webp", Picture(800, 800, SKEncodedImageFormat.Webp), "image/webp");
        await Until(() => message.Images.Count == 4 && vm.ImagePreviews.Count == 4 && vm.ImagePreviews[3].Bitmap != null);
        Console.WriteLine("PASS images: history, button, PNG/JPEG/WebP, corrupt file, live attachment, 1180/360px layout");
        var loaded = vm.ImagePreviews.ToArray();
        vm.CloseOverlayCommand.Execute(null);
        Expect(loaded.All(i => i.Bitmap == null) && vm.ImagePreviews.Count == 0, "Closing leaked image bitmaps");
        await Attach("Пятая.png", landscape, "image/png");
        await Attach("Шестая.png", landscape, "image/png");
        await Until(() => message.Images.Count == 6 && Thumbnails().Length == 4 && Thumbnails()[3]
            .GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "+2" && t.IsEffectivelyVisible));
        foreach (var width in new[] { 1180, 360 })
        {
            Resize(window, width);
            await Task.Delay(150);
            var album = shell.GetVisualDescendants().OfType<MessageImagePanel>().Single(p => p.Children.Count == 4);
            await Shot(window, $"images-album-six-{width}");
            // Child bounds are rounded independently at fractional Windows display scales.
            Expect(album.Bounds.Width <= 320 && album.Bounds.Height <= 288 &&
                album.Children[2].Bounds.Bottom <= album.Bounds.Height + 1,
                $"Many-image album exceeds bounds: {album.Bounds}; cells: {string.Join(';', album.Children.Select(c => c.Bounds))}");
        }
        Thumbnails()[3].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Expect(vm.ImagePreviews.Count == 6, "Hidden images are missing from full viewer");
        vm.CloseOverlayCommand.Execute(null);
        Console.WriteLine("PASS albums: cropped three-image mosaic, four visible cells, +2 overlay and all six in viewer");
        vm.ShowImagesCommand.Execute(message);
        var cancelled = vm.ImagePreviews.ToArray();
        vm.CloseOverlayCommand.Execute(null);
        await Task.Delay(200);
        Expect(cancelled.All(i => i.Bitmap == null) && vm.ImagePreviews.Count == 0, "Cancelled load repopulated sheet");
        vm.ShowImagesCommand.Execute(message);
        vm.SelectedChat = vm.Chats.First(c => c.Id != chatId);
        Expect(vm.Overlay == "" && vm.ImagePreviews.Count == 0, "Chat change left image sheet open");
        Console.WriteLine("PASS images: close/dispose, in-flight cancellation and chat change");

        vm.SelectedChat = vm.Chats.Single(c => c.Id == chatId);
        await Until(() => vm.Messages.Any(m => m.Id == messageId));
        message = vm.Messages.Single(m => m.Id == messageId);
        message.Images = [new AttachmentDto { Id = long.MaxValue, MessageId = messageId, FileName = "missing.png" }];
        vm.ShowImagesCommand.Execute(message);
        await Until(() => vm.ImagePreviews[0].Status != "Загрузка картинки…");
        Expect(vm.ImagePreviews[0].Bitmap == null && vm.ImagePreviews[0].Status?.Contains("Не удалось") == true,
            "HTTP 404 did not show an image error");
        await api.DeleteMessage(messageId, true);
        await Until(() => vm.Overlay == "" && vm.ImagePreviews.Count == 0);
        Console.WriteLine("PASS images: missing file and deletion while sheet is open");
        window.Close();
    }
}
