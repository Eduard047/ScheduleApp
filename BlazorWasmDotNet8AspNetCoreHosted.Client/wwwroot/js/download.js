// Створює Blob і запускає завантаження файла з байтів.
export function downloadFromBytes(fileName, contentType, byteArray) {
    if (!byteArray || byteArray.length === 0) {
        return;
    }
    const blob = new Blob([byteArray], { type: contentType });
    const objectUrl = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = objectUrl;
    link.download = fileName ?? "file.xlsx";
    document.body.appendChild(link);
    link.click();
    link.remove();
    // Даємо браузеру завершити обробку кліку перед звільненням Blob URL.
    window.setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
}
