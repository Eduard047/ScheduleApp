// Створює Blob і запускає завантаження файла з байтів.
export function downloadFromBytes(fileName, contentType, byteArray) {
    if (!byteArray || byteArray.length === 0) {
        return;
    }
    const blob = new Blob([byteArray], { type: contentType });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = fileName ?? "file.xlsx";
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
}
