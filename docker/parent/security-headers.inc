# Security headers of every parent app response (NFR-SEC-13). Included by each location of nginx.conf, because an
# add_header in a location replaces the server's headers. The e2e server (web/admin/e2e/serve.mjs) reads this file
# too, so the e2e suite runs under this policy.
#
# P2-F4 (DEC-352): invitation and password pages (P3-05) must never leak their URL to another site (Referrer-Policy).
# nosniff: files are served only as their declared type. frame-ancestors 'none' (and X-Frame-Options for older
# browsers): no site may frame the app, so a form cannot be clickjacked.
# P4-01 (DEC-502): the full Content-Security-Policy, stricter than the admin's: React sets styles through the DOM
# (allowed) and nothing adds <style> elements or style attributes, so style-src is 'self' too. Scripts, the service
# worker, the manifest, connections (the API and the SignalR WebSocket share the origin) and forms (the sandbox
# checkout posts to /api/v1/payment-callbacks) stay on this origin; the iyzico checkout is a navigation, which the
# policy does not restrict. The parent app shows no stored document, so no store origin. Permissions-Policy: the app
# uses no device feature.
add_header Referrer-Policy "no-referrer" always;
add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "DENY" always;
add_header Permissions-Policy "accelerometer=(), camera=(), display-capture=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), usb=()" always;
add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; font-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'" always;
