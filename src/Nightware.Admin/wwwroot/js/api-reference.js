// Starts Swagger UI on the API reference page (Pages/Api/Index.cshtml). A file, not inline script: the page's
// Content-Security-Policy allows scripts from this origin only.
(function () {
  var host = document.getElementById("swagger-ui");
  if (!host || typeof SwaggerUIBundle !== "function") {
    return;
  }

  SwaggerUIBundle({
    url: host.getAttribute("data-url"),
    domNode: host,
    deepLinking: true,
    docExpansion: "none",
    defaultModelsExpandDepth: -1,
    persistAuthorization: false,
    tryItOutEnabled: false,
    validatorUrl: null,
  });
})();
