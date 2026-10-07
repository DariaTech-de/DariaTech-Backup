// DariaTech presentation adapter. Duplicati copyright/license notices remain in About.
backupApp.service('BrandingService', function($timeout) {
    var brand = window.DariaTechBranding;
    var state = {
        appName: brand.productName,
        appSubtitle: brand.companyName,
        appLogoPath: '..' + brand.logo,
        supportUrl: brand.supportUrl,
        supportEmail: brand.supportEmail
    };
    this.state = state;
    this.watch = function(scope, callback) {
        scope.$on('brandingservicechanged', function() {
            if (callback) callback();
            $timeout(function() { scope.$digest(); });
        });
        if (callback) $timeout(callback);
        return state;
    };
});
