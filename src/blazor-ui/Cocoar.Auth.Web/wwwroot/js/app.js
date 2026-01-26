// QR Code generation function for 2FA setup
window.generateQRCode = function (elementId, text) {
    const container = document.getElementById(elementId);
    if (!container) {
        console.error('QR code container not found:', elementId);
        return;
    }

    // Clear any existing content
    container.innerHTML = '';

    // Create QR code using the qrcode library
    if (typeof QRCode !== 'undefined') {
        new QRCode(container, {
            text: text,
            width: 200,
            height: 200,
            colorDark: '#000000',
            colorLight: '#ffffff',
            correctLevel: QRCode.CorrectLevel.H
        });
    } else {
        console.error('QRCode library not loaded');
        container.innerHTML = '<p style="color: red;">QR Code library not available</p>';
    }
};

// Download file function for data export
window.downloadFile = function (fileName, contentType, base64Content) {
    const link = document.createElement('a');
    link.href = `data:${contentType};base64,${base64Content}`;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};
