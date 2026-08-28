function handler(event) {
  var request = event.request;
  var uri = request.uri;
  if (uri.indexOf('/titanium-web-proxy') !== 0) {
    if (uri === '/') {
      request.uri = '/titanium-web-proxy/';
    } else {
      request.uri = '/titanium-web-proxy' + uri;
    }
  }
  return request;
}
