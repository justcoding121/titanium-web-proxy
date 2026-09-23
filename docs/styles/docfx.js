// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
$(function () {
  var active = 'active';
  var expanded = 'in';
  var collapsed = 'collapsed';
  var filtered = 'filtered';
  var show = 'show';
  var hide = 'hide';
  var util = new utility();

  workAroundFixedHeaderForAnchors();
  highlight();
  enableSearch();

  renderTables();
  renderAlerts();
  renderLinks();
  renderNavbar();
  renderSidebar();
  renderAffix();
  renderFooter();
  renderLogo();

  breakText();
  renderTabs();

  window.refresh = function (article) {
    // Update markup result
    if (typeof article == 'undefined' || typeof article.content == 'undefined')
      console.error("Null Argument");
    $("article.content").html(article.content);

    highlight();
    renderTables();
    renderAlerts();
    renderAffix();
    renderTabs();
  }

  // Add this event listener when needed
  // window.addEventListener('content-update', contentUpdate);

  function breakText() {
    $(".xref").addClass("text-break");
    var texts = $(".text-break");
    texts.each(function () {
      $(this).breakWord();
    });
  }

  // Styling for tables in conceptual documents using Bootstrap.
  // See http://getbootstrap.com/css/#tables
  function renderTables() {
    $('table').addClass('table table-bordered table-condensed').wrap('<div class=\"table-responsive\"></div>');
  }

  // Styling for alerts.
  function renderAlerts() {
    $('.NOTE, .TIP').addClass('alert alert-info');
    $('.WARNING').addClass('alert alert-warning');
    $('.IMPORTANT, .CAUTION').addClass('alert alert-danger');
  }

  // Enable anchors for headings.
  (function () {
    anchors.options = {
      placement: 'left',
      visible: 'hover'
    };
    anchors.add('article h2:not(.no-anchor), article h3:not(.no-anchor), article h4:not(.no-anchor)');
  })();

  // Open links to different host in a new window.
  function renderLinks() {
    if ($("meta[property='docfx:newtab']").attr("content") === "true") {
      $(document.links).filter(function () {
        return this.hostname !== window.location.hostname;
      }).attr('target', '_blank');
    }
  }

  // Enable highlight.js
  function highlight() {
    $('pre code').each(function (i, block) {
      hljs.highlightElement(block);
    });
    $('pre code[highlight-lines]').each(function (i, block) {
      if (block.innerHTML === "") return;
      var lines = block.innerHTML.split('\n');

      queryString = block.getAttribute('highlight-lines');
      if (!queryString) return;

      var ranges = queryString.split(',');
      for (var j = 0, range; range = ranges[j++];) {
        var found = range.match(/^(\d+)\-(\d+)?$/);
        if (found) {
          // consider region as `{startlinenumber}-{endlinenumber}`, in which {endlinenumber} is optional
          var start = +found[1];
          var end = +found[2];
          if (isNaN(end) || end > lines.length) {
            end = lines.length;
          }
        } else {
          // consider region as a sigine line number
          if (isNaN(range)) continue;
          var start = +range;
          var end = start;
        }
        if (start <= 0 || end <= 0 || start > end || start > lines.length) {
          // skip current region if invalid
          continue;
        }
        lines[start - 1] = '<span class="line-highlight">' + lines[start - 1];
        lines[end - 1] = lines[end - 1] + '</span>';
      }

      block.innerHTML = lines.join('\n');
    });
  }

  // Support full-text-search
  function enableSearch() {
    var query;
    var relHref = $("meta[property='docfx\\:rel']").attr("content");
    if (typeof relHref === 'undefined') {
      return;
    }
    try {
      if(!window.Worker){
        return;
      }
      webWorkerSearch();
      renderSearchBox();
      highlightKeywords();
      addSearchEvent();
    } catch (e) {
      console.error(e);
    }

    //Adjust the position of search box in navbar
    function renderSearchBox() {
      autoCollapse();
      $(window).on('resize', autoCollapse);
      $(document).on('click', '.navbar-collapse.in', function (e) {
        if ($(e.target).is('a')) {
          $(this).collapse('hide');
        }
      });

      function autoCollapse() {
        var navbar = $('#autocollapse');
        if (navbar.height() === null) {
          setTimeout(autoCollapse, 300);
        }
        navbar.removeClass(collapsed);
        if (navbar.height() > 60) {
          navbar.addClass(collapsed);
        }
      }
    }

    function webWorkerSearch() {
      var indexReady = $.Deferred();

      var worker = new Worker(relHref + 'styles/search-worker.min.js');
      worker.onerror = function (oEvent) {
        console.error('Error occurred at search-worker. message: ' + oEvent.message);
      }

      worker.onmessage = function (oEvent) {
        switch (oEvent.data.e) {
          case 'index-ready':
            indexReady.resolve();
            break;
          case 'query-ready':
            var hits = oEvent.data.d;
            handleSearchResults(hits);
            break;
        }
      }

      indexReady.promise().done(function () {
        $("body").bind("queryReady", function () {
          worker.postMessage({ q: query });
        });
        if (query && (query.length >= 3)) {
          worker.postMessage({ q: query });
        }
      });
    }

    // Highlight the searching keywords
    function highlightKeywords() {
      var q = url('?q');
      if (q) {
        var keywords = q.split("%20");
        keywords.forEach(function (keyword) {
          if (keyword !== "") {
            $('.data-searchable *').mark(keyword);
            $('article *').mark(keyword);
          }
        });
      }
    }

    function addSearchEvent() {
      $('body').bind("searchEvent", function () {
        $('#search-query').keypress(function (e) {
          return e.which !== 13;
        });

        $('#search-query').keyup(function () {
          query = $(this).val();
          if (query === '') {
            flipContents("show");
          } else {
            flipContents("hide");
            $("body").trigger("queryReady");
            $('#search-results>.search-list>span').text('"' + query + '"');
          }
        }).off("keydown");
      });
    }

    function flipContents(action) {
      if (action === "show") {
        $('.hide-when-search').show();
        $('#search-results').hide();
      } else {
        $('.hide-when-search').hide();
        $('#search-results').show();
      }
    }

    function relativeUrlToAbsoluteUrl(currentUrl, relativeUrl) {
      var currentItems = currentUrl.split(/\/+/);
      var relativeItems = relativeUrl.split(/\/+/);
      var depth = currentItems.length - 1;
      var items = [];
      for (var i = 0; i < relativeItems.length; i++) {
        if (relativeItems[i] === '..') {
          depth--;
        } else if (relativeItems[i] !== '.') {
          items.push(relativeItems[i]);
        }
      }
      return currentItems.slice(0, depth).concat(items).join('/');
    }

    function extractContentBrief(content) {
      if (!content) {
        return
      }
      var briefOffset = 512;
      var words = query.split(/\s+/g);
      var queryIndex = content.indexOf(words[0]);
      var briefContent;
      if (queryIndex > briefOffset) {
        return "..." + content.slice(queryIndex - briefOffset, queryIndex + briefOffset) + "...";
      } else if (queryIndex <= briefOffset) {
        return content.slice(0, queryIndex + briefOffset) + "...";
      }
    }

    function handleSearchResults(hits) {
      var numPerPage = 10;
      var pagination = $('#pagination');
      pagination.empty();
      pagination.removeData("twbs-pagination");
      if (hits.length === 0) {
        $('#search-results>.sr-items').html('<p>No results found</p>');
      } else {
        pagination.twbsPagination({
          first: pagination.data('first'),
          prev: pagination.data('prev'),
          next: pagination.data('next'),
          last: pagination.data('last'),
          totalPages: Math.ceil(hits.length / numPerPage),
          visiblePages: 5,
          onPageClick: function (event, page) {
            var start = (page - 1) * numPerPage;
            var curHits = hits.slice(start, start + numPerPage);
            $('#search-results>.sr-items').empty().append(
              curHits.map(function (hit) {
                var currentUrl = window.location.href;
                var itemRawHref = relativeUrlToAbsoluteUrl(currentUrl, relHref + hit.href);
                var itemHref = relHref + hit.href + "?q=" + query;
                var itemTitle = hit.title;
                var itemBrief = extractContentBrief(hit.summary || '');

                var itemNode = $('<div>').attr('class', 'sr-item');
                var itemTitleNode = $('<div>').attr('class', 'item-title').append($('<a>').attr('href', itemHref).attr("target", "_blank").attr("rel", "noopener noreferrer").text(itemTitle));
                var itemHrefNode = $('<div>').attr('class', 'item-href').text(itemRawHref);
                var itemBriefNode = $('<div>').attr('class', 'item-brief').text(itemBrief);
                itemNode.append(itemTitleNode).append(itemHrefNode).append(itemBriefNode);
                return itemNode;
              })
            );
            query.split(/\s+/).forEach(function (word) {
              if (word !== '') {
                $('#search-results>.sr-items *').mark(word);
              }
            });
          }
        });
      }
    }
  };

  // Update href in navbar
  function renderNavbar() {
    var navbar = $('#navbar ul')[0];
    if (typeof (navbar) === 'undefined') {
      loadNavbar();
    } else {
      $('#navbar ul a.active').parents('li').addClass(active);
      renderBreadcrumb();
      showSearch();
    }

    function showSearch() {
      if ($('#search-results').length !== 0) {
          $('#search').show();
          $('body').trigger("searchEvent");
      }
    }

    function loadNavbar() {
      var navbarPath = $("meta[property='docfx\\:navrel']").attr("content");
      if (!navbarPath) {
        return;
      }
      navbarPath = navbarPath.replace(/\\/g, '/');
      var tocPath = $("meta[property='docfx\\:tocrel']").attr("content") || '';
      if (tocPath) tocPath = tocPath.replace(/\\/g, '/');
      $.get(navbarPath, function (data) {
        $(data).find("#toc>ul").appendTo("#navbar");
        showSearch();
        var index = navbarPath.lastIndexOf('/');
        var navrel = '';
        if (index > -1) {
          navrel = navbarPath.substr(0, index + 1);
        }
        $('#navbar>ul').addClass('navbar-nav');
        var currentAbsPath = util.getCurrentWindowAbsolutePath();
        // set active item
        $('#navbar').find('a[href]').each(function (i, e) {
          var href = $(e).attr("href");
          if (util.isRelativePath(href)) {
            href = navrel + href;
            $(e).attr("href", href);

            var isActive = false;
            var originalHref = e.name;
            if (originalHref) {
              originalHref = navrel + originalHref;
              if (util.getDirectory(util.getAbsolutePath(originalHref)) === util.getDirectory(util.getAbsolutePath(tocPath))) {
                isActive = true;
              }
            } else {
              if (util.getAbsolutePath(href) === currentAbsPath) {
                var dropdown = $(e).attr('data-toggle') == "dropdown"
                if (!dropdown) {
                  isActive = true;
                }
              }
            }
            if (isActive) {
              $(e).addClass(active);
            }
          }
        });
        renderNavbar();
      });
    }
  }

  function renderSidebar() {
    var sidetoc = $('#sidetoggle .sidetoc')[0];
    if (typeof (sidetoc) === 'undefined') {
      loadToc();
    } else {
      registerTocEvents();
      if ($('footer').is(':visible')) {
        $('.sidetoc').addClass('shiftup');
      }

      // Scroll to active item
      var top = 0;
      $('#toc a.active').parents('li').each(function (i, e) {
        $(e).addClass(active).addClass(expanded);
        $(e).children('a').addClass(active);
      })
      $('#toc a.active').parents('li').each(function (i, e) {
        top += $(e).position().top;
      })
      $('.sidetoc').scrollTop(top - 50);

      if ($('footer').is(':visible')) {
        $('.sidetoc').addClass('shiftup');
      }

      renderBreadcrumb();
    }

    function registerTocEvents() {
      var tocFilterInput = $('#toc_filter_input');
      var tocFilterClearButton = $('#toc_filter_clear');

      $('.toc .nav > li > .expand-stub').click(function (e) {
        $(e.target).parent().toggleClass(expanded);
      });
      $('.toc .nav > li > .expand-stub + a:not([href])').click(function (e) {
        $(e.target).parent().toggleClass(expanded);
      });
      tocFilterInput.on('input', function (e) {
        var val = this.value;
        //Save filter string to local session storage
        if (typeof(Storage) !== "undefined") {
          try {
            sessionStorage.filterString = val;
            }
          catch(e)
            {}
        }
        if (val === '') {
          // Clear 'filtered' class
          $('#toc li').removeClass(filtered).removeClass(hide);
          tocFilterClearButton.fadeOut();
          return;
        }
        tocFilterClearButton.fadeIn();

        // set all parent nodes status
        $('#toc li>a').filter(function (i, e) {
          return $(e).siblings().length > 0
        }).each(function (i, anchor) {
          var parent = $(anchor).parent();
          parent.addClass(hide);
          parent.removeClass(show);
          parent.removeClass(filtered);
        })

        // Get leaf nodes
        $('#toc li>a').filter(function (i, e) {
          return $(e).siblings().length === 0
        }).each(function (i, anchor) {
          var text = $(anchor).attr('title');
          var parent = $(anchor).parent();
          var parentNodes = parent.parents('ul>li');
          for (var i = 0; i < parentNodes.length; i++) {
            var parentText = $(parentNodes[i]).children('a').attr('title');
            if (parentText) text = parentText + '.' + text;
          };
          if (filterNavItem(text, val)) {
            parent.addClass(show);
            parent.removeClass(hide);
          } else {
            parent.addClass(hide);
            parent.removeClass(show);
          }
        });
        $('#toc li>a').filter(function (i, e) {
          return $(e).siblings().length > 0
        }).each(function (i, anchor) {
          var parent = $(anchor).parent();
          if (parent.find('li.show').length > 0) {
            parent.addClass(show);
            parent.addClass(filtered);
            parent.removeClass(hide);
          } else {
            parent.addClass(hide);
            parent.removeClass(show);
            parent.removeClass(filtered);
          }
        })

        function filterNavItem(name, text) {
          if (!text) return true;
          if (name && name.toLowerCase().indexOf(text.toLowerCase()) > -1) return true;
          return false;
        }
      });

      // toc filter clear button
      tocFilterClearButton.hide();
      tocFilterClearButton.on("click", function(e){
        tocFilterInput.val("");
        tocFilterInput.trigger('input');
        if (typeof(Storage) !== "undefined") {
          try {
            sessionStorage.filterString = "";
            }
          catch(e)
            {}
        }
      });

      //Set toc filter from local session storage on page load
      if (typeof(Storage) !== "undefined") {
        try {
          tocFilterInput.val(sessionStorage.filterString);
          tocFilterInput.trigger('input');
          }
        catch(e)
          {}
      }
    }

    function loadToc() {
      var tocPath = $("meta[property='docfx\\:tocrel']").attr("content");
      if (!tocPath) {
        return;
      }
      tocPath = tocPath.replace(/\\/g, '/');
      $('#sidetoc').load(tocPath + " #sidetoggle > div", function () {
        var index = tocPath.lastIndexOf('/');
        var tocrel = '';
        if (index > -1) {
          tocrel = tocPath.substr(0, index + 1);
        }
        var currentHref = util.getCurrentWindowAbsolutePath();
        if(!currentHref.endsWith('.html')) {
          currentHref += '.html';
        }
        $('#sidetoc').find('a[href]').each(function (i, e) {
          var href = $(e).attr("href");
          if (util.isRelativePath(href)) {
            href = tocrel + href;
            $(e).attr("href", href);
          }

          if (util.getAbsolutePath(e.href) === currentHref) {
            $(e).addClass(active);
          }

          $(e).breakWord();
        });

        renderSidebar();
      });
    }
  }

  function renderBreadcrumb() {
    var breadcrumb = [];
    $('#navbar a.active').each(function (i, e) {
      breadcrumb.push({
        href: e.href,
        name: e.innerHTML
      });
    })
    $('#toc a.active').each(function (i, e) {
      breadcrumb.push({
        href: e.href,
        name: e.innerHTML
      });
    })

    var html = util.formList(breadcrumb, 'breadcrumb');
    $('#breadcrumb').html(html);
  }

  //Setup Affix
  function renderAffix() {
    var hierarchy = getHierarchy();
    if (!hierarchy || hierarchy.length <= 0) {
      $("#affix").hide();
    }
    else {
      var html = util.formList(hierarchy, ['nav', 'bs-docs-sidenav']);
      $("#affix>div").empty().append(html);
      if ($('footer').is(':visible')) {
        $(".sideaffix").css("bottom", "70px");
      }
      $('#affix a').click(function(e) {
        var scrollspy = $('[data-spy="scroll"]').data()['bs.scrollspy'];
        var target = e.target.hash;
        if (scrollspy && target) {
          scrollspy.activate(target);
        }
      });
    }

    function getHierarchy() {
      // supported headers are h1, h2, h3, and h4
      var $headers = $($.map(['h1', 'h2', 'h3', 'h4'], function (h) { return ".article article " + h; }).join(", "));

      // a stack of hierarchy items that are currently being built
      var stack = [];
      $headers.each(function (i, e) {
        if (!e.id) {
          return;
        }

        var item = {
          name: htmlEncode($(e).text()),
          href: "#" + e.id,
          items: []
        };

        if (!stack.length) {
          stack.push({ type: e.tagName, siblings: [item] });
          return;
        }

        var frame = stack[stack.length - 1];
        if (e.tagName === frame.type) {
          frame.siblings.push(item);
        } else if (e.tagName[1] > frame.type[1]) {
          // we are looking at a child of the last element of frame.siblings.
          // push a frame onto the stack. After we've finished building this item's children,
          // we'll attach it as a child of the last element
          stack.push({ type: e.tagName, siblings: [item] });
        } else {  // e.tagName[1] < frame.type[1]
          // we are looking at a sibling of an ancestor of the current item.
          // pop frames from the stack, building items as we go, until we reach the correct level at which to attach this item.
          while (e.tagName[1] < stack[stack.length - 1].type[1]) {
            buildParent();
          }
          if (e.tagName === stack[stack.length - 1].type) {
            stack[stack.length - 1].siblings.push(item);
          } else {
            stack.push({ type: e.tagName, siblings: [item] });
          }
        }
      });
      while (stack.length > 1) {
        buildParent();
      }

      function buildParent() {
        var childrenToAttach = stack.pop();
        var parentFrame = stack[stack.length - 1];
        var parent = parentFrame.siblings[parentFrame.siblings.length - 1];
        $.each(childrenToAttach.siblings, function (i, child) {
          parent.items.push(child);
        });
      }
      if (stack.length > 0) {

        var topLevel = stack.pop().siblings;
        if (topLevel.length === 1) {  // if there's only one topmost header, dump it
          return topLevel[0].items;
        }
        return topLevel;
      }
      return undefined;
    }

    function htmlEncode(str) {
      if (!str) return str;
      return str
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
    }

    function htmlDecode(value) {
      if (!str) return str;
      return value
        .replace(/&quot;/g, '"')
        .replace(/&#39;/g, "'")
        .replace(/&lt;/g, '<')
        .replace(/&gt;/g, '>')
        .replace(/&amp;/g, '&');
    }

    function cssEscape(str) {
      // see: http://stackoverflow.com/questions/2786538/need-to-escape-a-special-character-in-a-jquery-selector-string#answer-2837646
      if (!str) return str;
      return str
        .replace(/[!"#$%&'()*+,.\/:;<=>?@[\\\]^`{|}~]/g, "\\$&");
    }
  }

  // Show footer
  function renderFooter() {
    initFooter();
    $(window).on("scroll", showFooterCore);

    function initFooter() {
      if (needFooter()) {
        shiftUpBottomCss();
        $("footer").show();
      } else {
        resetBottomCss();
        $("footer").hide();
      }
    }

    function showFooterCore() {
      if (needFooter()) {
        shiftUpBottomCss();
        $("footer").fadeIn();
      } else {
        resetBottomCss();
        $("footer").fadeOut();
      }
    }

    function needFooter() {
      var scrollHeight = $(document).height();
      var scrollPosition = $(window).height() + $(window).scrollTop();
      return (scrollHeight - scrollPosition) < 1;
    }

    function resetBottomCss() {
      $(".sidetoc").removeClass("shiftup");
      $(".sideaffix").removeClass("shiftup");
    }

    function shiftUpBottomCss() {
      $(".sidetoc").addClass("shiftup");
      $(".sideaffix").addClass("shiftup");
    }
  }

  function renderLogo() {
    // For LOGO SVG
    // Replace SVG with inline SVG
    // http://stackoverflow.com/questions/11978995/how-to-change-color-of-svg-image-using-css-jquery-svg-image-replacement
    jQuery('img.svg').each(function () {
      var $img = jQuery(this);
      var imgID = $img.attr('id');
      var imgClass = $img.attr('class');
      var imgURL = $img.attr('src');

      jQuery.get(imgURL, function (data) {
        // Get the SVG tag, ignore the rest
        var $svg = jQuery(data).find('svg');

        // Add replaced image's ID to the new SVG
        if (typeof imgID !== 'undefined') {
          $svg = $svg.attr('id', imgID);
        }
        // Add replaced image's classes to the new SVG
        if (typeof imgClass !== 'undefined') {
          $svg = $svg.attr('class', imgClass + ' replaced-svg');
        }

        // Remove any invalid XML tags as per http://validator.w3.org
        $svg = $svg.removeAttr('xmlns:a');

        // Replace image with new SVG
        $img.replaceWith($svg);

      }, 'xml');
    });
  }

  function renderTabs() {
    var contentAttrs = {
      id: 'data-bi-id',
      name: 'data-bi-name',
      type: 'data-bi-type'
    };

    var Tab = (function () {
      function Tab(li, a, section) {
        this.li = li;
        this.a = a;
        this.section = section;
      }
      Object.defineProperty(Tab.prototype, "tabIds", {
        get: function () { return this.a.getAttribute('data-tab').split(' '); },
        enumerable: true,
        configurable: true
      });
      Object.defineProperty(Tab.prototype, "condition", {
        get: function () { return this.a.getAttribute('data-condition'); },
        enumerable: true,
        configurable: true
      });
      Object.defineProperty(Tab.prototype, "visible", {
        get: function () { return !this.li.hasAttribute('hidden'); },
        set: function (value) {
          if (value) {
            this.li.removeAttribute('hidden');
            this.li.removeAttribute('aria-hidden');
          }
          else {
            this.li.setAttribute('hidden', 'hidden');
            this.li.setAttribute('aria-hidden', 'true');
          }
        },
        enumerable: true,
        configurable: true
      });
      Object.defineProperty(Tab.prototype, "selected", {
        get: function () { return !this.section.hasAttribute('hidden'); },
        set: function (value) {
          if (value) {
            this.a.setAttribute('aria-selected', 'true');
            this.a.tabIndex = 0;
            this.section.removeAttribute('hidden');
            this.section.removeAttribute('aria-hidden');
          }
          else {
            this.a.setAttribute('aria-selected', 'false');
            this.a.tabIndex = -1;
            this.section.setAttribute('hidden', 'hidden');
            this.section.setAttribute('aria-hidden', 'true');
          }
        },
        enumerable: true,
        configurable: true
      });
      Tab.prototype.focus = function () {
        this.a.focus();
      };
      return Tab;
    }());

    initTabs(document.body);

    function initTabs(container) {
      var queryStringTabs = readTabsQueryStringParam();
      var elements = container.querySelectorAll('.tabGroup');
      var state = { groups: [], selectedTabs: [] };
      for (var i = 0; i < elements.length; i++) {
        var group = initTabGroup(elements.item(i));
        if (!group.independent) {
          updateVisibilityAndSelection(group, state);
          state.groups.push(group);
        }
      }
      container.addEventListener('click', function (event) { return handleClick(event, state); });
      if (state.groups.length === 0) {
        return state;
      }
      selectTabs(queryStringTabs, container);
      updateTabsQueryStringParam(state);
      notifyContentUpdated();
      return state;
    }

    function initTabGroup(element) {
      var group = {
        independent: element.hasAttribute('data-tab-group-independent'),
        tabs: []
      };
      var li = element.firstElementChild.firstElementChild;
      while (li) {
        var a = li.firstElementChild;
        a.setAttribute(contentAttrs.name, 'tab');
        var dataTab = a.getAttribute('data-tab').replace(/\+/g, ' ');
        a.setAttribute('data-tab', dataTab);
        var section = element.querySelector("[id=\"" + a.getAttribute('aria-controls') + "\"]");
        var tab = new Tab(li, a, section);
        group.tabs.push(tab);
        li = li.nextElementSibling;
      }
      element.setAttribute(contentAttrs.name, 'tab-group');
      element.tabGroup = group;
      return group;
    }

    function updateVisibilityAndSelection(group, state) {
      var anySelected = false;
      var firstVisibleTab;
      for (var _i = 0, _a = group.tabs; _i < _a.length; _i++) {
        var tab = _a[_i];
        tab.visible = tab.condition === null || state.selectedTabs.indexOf(tab.condition) !== -1;
        if (tab.visible) {
          if (!firstVisibleTab) {
            firstVisibleTab = tab;
          }
        }
        tab.selected = tab.visible && arraysIntersect(state.selectedTabs, tab.tabIds);
        anySelected = anySelected || tab.selected;
      }
      if (!anySelected) {
        for (var _b = 0, _c = group.tabs; _b < _c.length; _b++) {
          var tabIds = _c[_b].tabIds;
          for (var _d = 0, tabIds_1 = tabIds; _d < tabIds_1.length; _d++) {
            var tabId = tabIds_1[_d];
            var index = state.selectedTabs.indexOf(tabId);
            if (index === -1) {
              continue;
            }
            state.selectedTabs.splice(index, 1);
          }
        }
        var tab = firstVisibleTab;
        tab.selected = true;
        state.selectedTabs.push(tab.tabIds[0]);
      }
    }

    function getTabInfoFromEvent(event) {
      if (!(event.target instanceof HTMLElement)) {
        return null;
      }
      var anchor = event.target.closest('a[data-tab]');
      if (anchor === null) {
        return null;
      }
      var tabIds = anchor.getAttribute('data-tab').split(' ');
      var group = anchor.parentElement.parentElement.parentElement.tabGroup;
      if (group === undefined) {
        return null;
      }
      return { tabIds: tabIds, group: group, anchor: anchor };
    }

    function handleClick(event, state) {
      var info = getTabInfoFromEvent(event);
      if (info === null) {
        return;
      }
      event.preventDefault();
      info.anchor.href = 'javascript:';
      setTimeout(function () { return info.anchor.href = '#' + info.anchor.getAttribute('aria-controls'); });
      var tabIds = info.tabIds, group = info.group;
      var originalTop = info.anchor.getBoundingClientRect().top;
      if (group.independent) {
        for (var _i = 0, _a = group.tabs; _i < _a.length; _i++) {
          var tab = _a[_i];
          tab.selected = arraysIntersect(tab.tabIds, tabIds);
        }
      }
      else {
        if (arraysIntersect(state.selectedTabs, tabIds)) {
          return;
        }
        var previousTabId = group.tabs.filter(function (t) { return t.selected; })[0].tabIds[0];
        state.selectedTabs.splice(state.selectedTabs.indexOf(previousTabId), 1, tabIds[0]);
        for (var _b = 0, _c = state.groups; _b < _c.length; _b++) {
          var group_1 = _c[_b];
          updateVisibilityAndSelection(group_1, state);
        }
        updateTabsQueryStringParam(state);
      }
      notifyContentUpdated();
      var top = info.anchor.getBoundingClientRect().top;
      if (top !== originalTop && event instanceof MouseEvent) {
        window.scrollTo(0, window.pageYOffset + top - originalTop);
      }
    }

    function selectTabs(tabIds) {
      for (var _i = 0, tabIds_1 = tabIds; _i < tabIds_1.length; _i++) {
        var tabId = tabIds_1[_i];
        var a = document.querySelector(".tabGroup > ul > li > a[data-tab=\"" + tabId + "\"]:not([hidden])");
        if (a === null) {
          return;
        }
        a.dispatchEvent(new CustomEvent('click', { bubbles: true }));
      }
    }

    function readTabsQueryStringParam() {
      var qs = parseQueryString(window.location.search);
      var t = qs.tabs;
      if (t === undefined || t === '') {
        return [];
      }
      return t.split(',');
    }

    function updateTabsQueryStringParam(state) {
      var qs = parseQueryString(window.location.search);
      qs.tabs = state.selectedTabs.join();
      var url = location.protocol + "//" + location.host + location.pathname + "?" + toQueryString(qs) + location.hash;
      if (location.href === url) {
        return;
      }
      history.replaceState({}, document.title, url);
    }

    function toQueryString(args) {
      var parts = [];
      for (var name_1 in args) {
        if (args.hasOwnProperty(name_1) && args[name_1] !== '' && args[name_1] !== null && args[name_1] !== undefined) {
          parts.push(encodeURIComponent(name_1) + '=' + encodeURIComponent(args[name_1]));
        }
      }
      return parts.join('&');
    }

    function parseQueryString(queryString) {
      var match;
      var pl = /\+/g;
      var search = /([^&=]+)=?([^&]*)/g;
      var decode = function (s) { return decodeURIComponent(s.replace(pl, ' ')); };
      if (queryString === undefined) {
        queryString = '';
      }
      queryString = queryString.substring(1);
      var urlParams = {};
      while (match = search.exec(queryString)) {
        urlParams[decode(match[1])] = decode(match[2]);
      }
      return urlParams;
    }

    function arraysIntersect(a, b) {
      for (var _i = 0, a_1 = a; _i < a_1.length; _i++) {
        var itemA = a_1[_i];
        for (var _a = 0, b_1 = b; _a < b_1.length; _a++) {
          var itemB = b_1[_a];
          if (itemA === itemB) {
            return true;
          }
        }
      }
      return false;
    }

    function notifyContentUpdated() {
      // Dispatch this event when needed
      // window.dispatchEvent(new CustomEvent('content-update'));
    }
  }

  function utility() {
    this.getAbsolutePath = getAbsolutePath;
    this.isRelativePath = isRelativePath;
    this.isAbsolutePath = isAbsolutePath;
    this.getCurrentWindowAbsolutePath = getCurrentWindowAbsolutePath;
    this.getDirectory = getDirectory;
    this.formList = formList;

    function getAbsolutePath(href) {
      if (isAbsolutePath(href)) return href;
      var currentAbsPath = getCurrentWindowAbsolutePath();
      var stack = currentAbsPath.split("/");
      stack.pop();
      var parts = href.split("/");
      for (var i=0; i< parts.length; i++) {
        if (parts[i] == ".") continue;
        if (parts[i] == ".." && stack.length > 0)
          stack.pop();
        else
          stack.push(parts[i]);
      }
      var p = stack.join("/");
      return p;
    }

    function isRelativePath(href) {
      if (href === undefined || href === '' || href[0] === '/') {
        return false;
      }
      return !isAbsolutePath(href);
    }

    function isAbsolutePath(href) {
      return (/^(?:[a-z]+:)?\/\//i).test(href);
    }

    function getCurrentWindowAbsolutePath() {
      return window.location.origin + window.location.pathname;
    }
    function getDirectory(href) {
      if (!href) return '';
      var index = href.lastIndexOf('/');
      if (index == -1) return '';
      if (index > -1) {
        return href.substr(0, index);
      }
    }

    function formList(item, classes) {
      var level = 1;
      var model = {
        items: item
      };
      var cls = [].concat(classes).join(" ");
      return getList(model, cls);

      function getList(model, cls) {
        if (!model || !model.items) return null;
        var l = model.items.length;
        if (l === 0) return null;
        var html = '<ul class="level' + level + ' ' + (cls || '') + '">';
        level++;
        for (var i = 0; i < l; i++) {
          var item = model.items[i];
          var href = item.href;
          var name = item.name;
          if (!name) continue;
          html += href ? '<li><a href="' + href + '">' + name + '</a>' : '<li>' + name;
          html += getList(item, cls) || '';
          html += '</li>';
        }
        html += '</ul>';
        return html;
      }
    }

    /**
     * Add <wbr> into long word.
     * @param {String} text - The word to break. It should be in plain text without HTML tags.
     */
    function breakPlainText(text) {
      if (!text) return text;
      return text.replace(/([a-z])([A-Z])|(\.)(\w)/g, '$1$3<wbr>$2$4')
    }

    /**
     * Add <wbr> into long word. The jQuery element should contain no html tags.
     * If the jQuery element contains tags, this function will not change the element.
     */
    $.fn.breakWord = function () {
      if (!this.html().match(/(<\w*)((\s\/>)|(.*<\/\w*>))/g)) {
        this.html(function (index, text) {
          return breakPlainText(text);
        })
      }
      return this;
    }
  }

  // adjusted from https://stackoverflow.com/a/13067009/1523776
  function workAroundFixedHeaderForAnchors() {
    var HISTORY_SUPPORT = !!(history && history.pushState);
    var ANCHOR_REGEX = /^#[^ ]+$/;

    function getFixedOffset() {
      return $('header').first().height();
    }

    /**
     * If the provided href is an anchor which resolves to an element on the
     * page, scroll to it.
     * @param  {String} href
     * @return {Boolean} - Was the href an anchor.
     */
    function scrollIfAnchor(href, pushToHistory) {
      var match, rect, anchorOffset;

      if (!ANCHOR_REGEX.test(href)) {
        return false;
      }

      match = document.getElementById(href.slice(1));

      if (match) {
        rect = match.getBoundingClientRect();
        anchorOffset = window.pageYOffset + rect.top - getFixedOffset();
        window.scrollTo(window.pageXOffset, anchorOffset);

        // Add the state to history as-per normal anchor links
        if (HISTORY_SUPPORT && pushToHistory) {
          history.pushState({}, document.title, location.pathname + href);
        }
      }

      return !!match;
    }

    /**
     * Attempt to scroll to the current location's hash.
     */
    function scrollToCurrent() {
      scrollIfAnchor(window.location.hash);
    }

    /**
     * If the click event's target was an anchor, fix the scroll position.
     */
    function delegateAnchors(e) {
      var elem = e.target;

      if (scrollIfAnchor(elem.getAttribute('href'), true)) {
        e.preventDefault();
      }
    }

    $(window).on('hashchange', scrollToCurrent);

    $(window).on('load', function () {
        // scroll to the anchor if present, offset by the header
        scrollToCurrent();
    });

    $(document).ready(function () {
        // Exclude tabbed content case
        $('a:not([data-tab])').click(function (e) { delegateAnchors(e); });
    });
  }
});

// SIG // Begin signature block
// SIG // MIIvHwYJKoZIhvcNAQcCoIIvEDCCLwwCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // qNF/FaPC5umf/+IBERZBF6ZojLt+sXa1gsWWhq8OII+g
// SIG // ghOgMIIFZDCCA0ygAwIBAgIQBs7hMb5tVcgH98DH+0Tm
// SIG // IDANBgkqhkiG9w0BAQwFADBMMQswCQYDVQQGEwJVUzEX
// SIG // MBUGA1UEChMORGlnaUNlcnQsIEluYy4xJDAiBgNVBAMT
// SIG // G0RpZ2lDZXJ0IENTIFJTQTQwOTYgUm9vdCBHNTAeFw0y
// SIG // MTAxMTUwMDAwMDBaFw00NjAxMTQyMzU5NTlaMEwxCzAJ
// SIG // BgNVBAYTAlVTMRcwFQYDVQQKEw5EaWdpQ2VydCwgSW5j
// SIG // LjEkMCIGA1UEAxMbRGlnaUNlcnQgQ1MgUlNBNDA5NiBS
// SIG // b290IEc1MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIIC
// SIG // CgKCAgEAtjNzgNhiA3AULBEcOV58rnyDhh3+Ji9MJK2L
// SIG // 6oNfqbw9W/wLmEwCRzDs4v7s6DRbZl6/O9cspiX/jFmz
// SIG // 3+rafCnZRlByCB1u0RsK3R/NmYn6Dw9zxOGcHXUyzW+X
// SIG // 2ipqlbJsyQnQ6gt7fRcGSZnv1t7gyFPUrsZ38Ya7Ixy4
// SIG // wN9Z94590e+C5iaLWji1/3XVstlPCfM3iFDaEaSKFBTR
// SIG // UwQAffNqRBj+UHAyBxyomg46HcUKH24LJmm3PKJXcCyG
// SIG // +kxulalYQ7msEtb/P+3XQxdrTM6exJCr//oQUJqjkFfW
// SIG // 54wQrp8WGs81HX/Xdu2KnDWnKLinXSH8MDfd3ggZTxXG
// SIG // 56bakEeO95RTTI5TAr79meXqhtCvAwLTm6qT8asojiAB
// SIG // /0z7zLcpQPWHpBITBR9DbtdRUJ84tCDtFwkSj8y5Ga+f
// SIG // zb5pEdOvVRBtF4Z5llLGsgCd5a84sDX0iGuPDgQ9fO6v
// SIG // zdNqEErGzYbKIj2hSlz7Dv+I31xip8C5HtmsbH44N/53
// SIG // kyXChYpPtTcGWgaBFPHOlJ2ZkeoyWs5nPW4EZq0MTy2j
// SIG // Lvee9Xid9wr9fo/jQopVlrzxnzct/J5flf6MGBv8jv1L
// SIG // kK/XA2gSY6zik6eiywTlT2TOA/rGFJ/Zi+jM1GKMa+QA
// SIG // LBmfGgbGMYFU+1Mkmq9Vmbqdda64wt0CAwEAAaNCMEAw
// SIG // HQYDVR0OBBYEFGgBk7HSSkBCaZRGLBxaiKkltEdPMA4G
// SIG // A1UdDwEB/wQEAwIBhjAPBgNVHRMBAf8EBTADAQH/MA0G
// SIG // CSqGSIb3DQEBDAUAA4ICAQCS/O64AnkXAlF9IcVJZ6ek
// SIG // 8agkOOsMaOpaQmuc9HPBaUotszcFUEKYkp4GeSwuBpn2
// SIG // 798roM2zkgGDtaDLJ7U8IxqYSaLsLZmlWUOs0rGT1lfX
// SIG // HLyT1sZA4bNvGVW3E9flQzOktavL2sExZA101iztw41u
// SIG // 67uvGUdhYS3A9AW5b3jcOvdCQGVTkb2ZDZOSVKapN1kr
// SIG // m8uZxrw99wSE8JQzHQ+CWjnLLkXDKBmjspuYyPwxa2CP
// SIG // 9umGKLzgPH10XRaJW2kkxxCLxEu7Nk/UWT/DsKSRmfgu
// SIG // 0UoBnfWIEu+/WhFqWU9Za1pn84+0Ew/A2C89KHKqGX8R
// SIG // fWpbn5XnX7eUT/E+oVr/Lcyd3yd3jzJzHGcKdvP6XLG/
// SIG // vB29DCibsscXZwszD8O9Ntz7ukILq+2Ew2LWhBapsQdr
// SIG // qW7uxs/msEQpwvCzYYAqi2/SFFwlh1Rk86RMwaH4p2vq
// SIG // /uo6/HnbDo/cxvPJ1Gze6YOhjh0i7Mk6sgB73DunQhp/
// SIG // 3IupET2Op8Agb10JXUNE5o9mzKlbB/Hvm3oOs1ThlP0O
// SIG // LMaT11X9cZg1uAlK/8YpKCz2Ui3bFBiSJ+IWfozK1GG+
// SIG // goeR65g3P79fXXc/NKwbOEOraHKZMh46GhmlozhMI9ej
// SIG // 58zVKpIXkAtaS70WvfuGauKJmezkoFUYyaMIHxPgMghy
// SIG // 0DCCBpAwggR4oAMCAQICEAreMulQm0SqNLHa8bwOyHMw
// SIG // DQYJKoZIhvcNAQELBQAwTDELMAkGA1UEBhMCVVMxFzAV
// SIG // BgNVBAoTDkRpZ2lDZXJ0LCBJbmMuMSQwIgYDVQQDExtE
// SIG // aWdpQ2VydCBDUyBSU0E0MDk2IFJvb3QgRzUwHhcNMjEw
// SIG // NzE1MDAwMDAwWhcNMzEwNzE0MjM1OTU5WjBbMQswCQYD
// SIG // VQQGEwJVUzEYMBYGA1UEChMPLk5FVCBGb3VuZGF0aW9u
// SIG // MTIwMAYDVQQDEykuTkVUIEZvdW5kYXRpb24gUHJvamVj
// SIG // dHMgQ29kZSBTaWduaW5nIENBMjCCAiIwDQYJKoZIhvcN
// SIG // AQEBBQADggIPADCCAgoCggIBAM55KccMg/kCnTurEsDw
// SIG // SQ+cLSS/Z3MiJYRxTPK1r1OZQgqDKfU3AIl7wiCGiQnj
// SIG // sY1xb9Jmq2hoUq/EgDVXnwu+BwXw0wJfycqFpjnwcUWP
// SIG // 99P9Kor31LaMa8PdUkdxrt3rnFscW+G/oaCKU4GylPv/
// SIG // 3J0r4vBJgU5O57XxZAFsP+i6L/pzF1PqhM5v07gk/Kll
// SIG // 9iH3y5HVyLeFKkEvCao8Z+3Bi4Y6gwPcPuc16UsUrvyY
// SIG // SjnnGNOV3HRpRp0AT3PEQc1A92c1zD6DwUhYh+1GbNt4
// SIG // lZlVwiaFPSBWjd1kiw/42d01xPEcScaGNQzg8K+LepE0
// SIG // kHQz68d2j67QHCRW5YdyTD9EqWWaVfAxwC1WZCinxbtk
// SIG // ji/Vr+ezCejP5oX8lKLdry1cVZVpaZYMDoXBz+2uNHFQ
// SIG // XMxf3AG/iAc/rbgdasBhouAd6pxNpKWI7usWM++YVXHx
// SIG // alxpst/jTGSBeqDCVEi1GZlOx1AwlMjIB7ekj+Nq+780
// SIG // cjyRDFwEYjcHuYyOqAFscnl+nTeGV4gQ2tZVWlbivMlH
// SIG // e0oWxEz20z2meQWlf8sXJda6hMT6isyB/sv2uhd9yl0m
// SIG // FRk0hXIP7UhHeL7l2wVLiH9lKT1ZCvidv9Cf6PLAUpBd
// SIG // 5xWf1EsK73DChIiDSqJB75or8qS5IUnzCBIrJl0juq5H
// SIG // vBJRAgMBAAGjggFdMIIBWTASBgNVHRMBAf8ECDAGAQH/
// SIG // AgEAMB0GA1UdDgQWBBQoDkyJHJmX8YHwjwjeVxJLvLh4
// SIG // zzAfBgNVHSMEGDAWgBRoAZOx0kpAQmmURiwcWoipJbRH
// SIG // TzAOBgNVHQ8BAf8EBAMCAYYwEwYDVR0lBAwwCgYIKwYB
// SIG // BQUHAwMweQYIKwYBBQUHAQEEbTBrMCQGCCsGAQUFBzAB
// SIG // hhhodHRwOi8vb2NzcC5kaWdpY2VydC5jb20wQwYIKwYB
// SIG // BQUHMAKGN2h0dHA6Ly9jYWNlcnRzLmRpZ2ljZXJ0LmNv
// SIG // bS9EaWdpQ2VydENTUlNBNDA5NlJvb3RHNS5jcnQwRQYD
// SIG // VR0fBD4wPDA6oDigNoY0aHR0cDovL2NybDMuZGlnaWNl
// SIG // cnQuY29tL0RpZ2lDZXJ0Q1NSU0E0MDk2Um9vdEc1LmNy
// SIG // bDAcBgNVHSAEFTATMAcGBWeBDAEDMAgGBmeBDAEEATAN
// SIG // BgkqhkiG9w0BAQsFAAOCAgEAOuoiUIbgalnJ/q7pK5M7
// SIG // /P9ghqclk6qKLKb7fja9s7IJdcV/e7+fPJZ4LPeLqpUw
// SIG // fdGC0eNtuz2UMx0ABRLNCtP1DVH6CjwvQ2Up+MScYz7C
// SIG // 2ivDfpeHeqGQQG+wF4JacX5OKHK8zNClmE52s9/pbdLh
// SIG // ovFYD/FoIIduBS8c8cYguUqsGe8H1O8NYO2LoA5+sW3B
// SIG // +PsnemaQpzA3MWJBiqjrYzO1d23d0W5fzw8kmfnIcv5+
// SIG // kOmTY/Ts/1hWJWDiHewjJiOUs2zFqam3cLzzZK0mMBUb
// SIG // Z0Vfa8CaP6vY3LX8KxvRUAZBzF4ix0UD8OKBKETpNAGA
// SIG // ptus73RKnNaTwiBEB7V8jlvhql9jDkUXYnjxX5hvG9cZ
// SIG // SMPIgSTqK5WxCQOrbzqQ029SOfqYerTOA8KzK0tC2xjy
// SIG // b9GofX4dumT7WtcRdCguP4HcqmOwZoo/l/mrXa0WdI3a
// SIG // nEh7KVmHPVOkml1ilVxcL5HNG9fXZMxAku3m6z0LgtPn
// SIG // ri6bwzMxyGn8y9tVxwdfoJ3LsG7e/0ZJyaA2xSrMecdK
// SIG // g0Or3yZq2o8jQDHeXxkHYHFB7O+Rvilr4OzOEbRkaujR
// SIG // 615wYNSx0JUWyw0gl2yKF5MsM85LCrscZvbJ9oHc9P7+
// SIG // KOLz0B68ZPDI+ott1Oofe7iUFlT4S1oULXI3PDCZlNou
// SIG // cZQwggegMIIFiKADAgECAhAOAvCsREVf/ih65u/I72ei
// SIG // MA0GCSqGSIb3DQEBCwUAMFsxCzAJBgNVBAYTAlVTMRgw
// SIG // FgYDVQQKEw8uTkVUIEZvdW5kYXRpb24xMjAwBgNVBAMT
// SIG // KS5ORVQgRm91bmRhdGlvbiBQcm9qZWN0cyBDb2RlIFNp
// SIG // Z25pbmcgQ0EyMB4XDTI2MDkxNzAwMDAwMFoXDTI3MTIx
// SIG // OTIzNTk1OVowgd8xEzARBgsrBgEEAYI3PAIBAxMCVVMx
// SIG // GzAZBgsrBgEEAYI3PAIBAhMKV2FzaGluZ3RvbjEdMBsG
// SIG // A1UEDwwUUHJpdmF0ZSBPcmdhbml6YXRpb24xFDASBgNV
// SIG // BAUTCzYwMyAzODkgMDY4MQswCQYDVQQGEwJVUzETMBEG
// SIG // A1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9u
// SIG // ZDEgMB4GA1UEChMXRG9jRlggKC5ORVQgRm91bmRhdGlv
// SIG // bikxIDAeBgNVBAMTF0RvY0ZYICguTkVUIEZvdW5kYXRp
// SIG // b24pMIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKC
// SIG // AgEAs01497/7/JVvAj5GPfrFDIq+xb/h99PjLs/ayrmw
// SIG // 0v4gY3WOnVGKDAvPyhRpJWWuTLSNspfn36J3ak44gIfm
// SIG // JM4nfwPKuAZNGU2iyCbuzB6tFX3Pmd75tS+b0dBvp7kn
// SIG // bWmKUEfy6APniJtE2CURqf20AJ5CeW0fMf2c+HQrMyUE
// SIG // byAbrRnRhK4+nBtHOZP2eG9NbmFzsf4Wkt5+PVwN4M46
// SIG // IvFTAIliiQ2OfRNJfezUObjrrNjRng7ODlk6IHFVY41z
// SIG // a1BaXOaVWe1KXmwwNk17DGVFMwlzj7uWGw7mYjSpv17a
// SIG // GVdLcZgqBdBDny+mSG5fYnz5BNUAZZh1r6/Onyjneraj
// SIG // 3AK9bsMtmTL75BW2XvYdJQwA7/3SDIiNXn32nE7r+iZO
// SIG // G344P0gTZKznH9FBI0HXVYJJ/5//v0kYEXdQPvBmT9lO
// SIG // 5PnKBoZG9vYF6bfVc2/kHXjNwiM0w/LGGey3b4BPhbFU
// SIG // pu7POYaUiy9QtIgoJkMQAeCR6WvisJcorbgjaLy3emw9
// SIG // uuNUFK47G+REZz5rvGG6qwvmJgr3M4aLjdPd9X+WXWJV
// SIG // uVodv8OhcNA265FX+8tfPyYVudmNNieuBdigFqZmWtH2
// SIG // tgU5JfeHHjpPayM1UH5xHTFbMisFXT9e+PVR/vgXcGsA
// SIG // 5EITiWrehX7Z30oWoSjdaqA2IQsCAwEAAaOCAdkwggHV
// SIG // MB8GA1UdIwQYMBaAFCgOTIkcmZfxgfCPCN5XEku8uHjP
// SIG // MB0GA1UdDgQWBBSd4GagOx0ybONkN4zAjFIXCH/c5zA9
// SIG // BgNVHSAENjA0MDIGBWeBDAEDMCkwJwYIKwYBBQUHAgEW
// SIG // G2h0dHA6Ly93d3cuZGlnaWNlcnQuY29tL0NQUzAOBgNV
// SIG // HQ8BAf8EBAMCB4AwEwYDVR0lBAwwCgYIKwYBBQUHAwMw
// SIG // gZsGA1UdHwSBkzCBkDBGoESgQoZAaHR0cDovL2NybDMu
// SIG // ZGlnaWNlcnQuY29tL05FVEZvdW5kYXRpb25Qcm9qZWN0
// SIG // c0NvZGVTaWduaW5nQ0EyLmNybDBGoESgQoZAaHR0cDov
// SIG // L2NybDQuZGlnaWNlcnQuY29tL05FVEZvdW5kYXRpb25Q
// SIG // cm9qZWN0c0NvZGVTaWduaW5nQ0EyLmNybDCBhQYIKwYB
// SIG // BQUHAQEEeTB3MCQGCCsGAQUFBzABhhhodHRwOi8vb2Nz
// SIG // cC5kaWdpY2VydC5jb20wTwYIKwYBBQUHMAKGQ2h0dHA6
// SIG // Ly9jYWNlcnRzLmRpZ2ljZXJ0LmNvbS9ORVRGb3VuZGF0
// SIG // aW9uUHJvamVjdHNDb2RlU2lnbmluZ0NBMi5jcnQwCQYD
// SIG // VR0TBAIwADANBgkqhkiG9w0BAQsFAAOCAgEAdl5B6aog
// SIG // v4Zm8KehTf+xDovKjRI0YFNGg1ll5KwXKjPhDo5St2UF
// SIG // 6ZW2lT7r5NMs0a4fEJG9LEtLBdBV+ErBcEtj80etYkHe
// SIG // gUI7RXz+Eit/HJIDN6Vk0RuLD+tzR9AKwSHBmRLIe0Eg
// SIG // 7rfmjyOpZcSto3eBLossUFHn6j2zuHWwJdo9NWlyn+SH
// SIG // Q0ZEdFZR3eyvecCGTnyGmUI8U+swXxkZAC8+YpM59kgz
// SIG // xAps/ITkgclzb5VK4A9XHdaQ3XWUTheRQ1qnrvFtzdDN
// SIG // B2+1B+JinQRr7gkRNeRC7E78yNduz+UQEAEbyz9WVWOS
// SIG // vXSFTLTIEPxHD5yMJSe+T4TSrPgwj9YpaBg8/w9vgBZy
// SIG // ogVc4ujXmjRDK104+7RoW8Vcsglx2PDjNvM3/0rsYbu7
// SIG // XgR5mOpASxE2UQ5cuF0y7q4WzHpNV+i71rORaIlvldF+
// SIG // w1a9+qyqg9LE5Y67pZzQBNSZtvS9nrBMVpBaaNHan05o
// SIG // nK7Z5nGWGkFd5Qj16cwm+ekB/8oGJyulf2og6HU8luE+
// SIG // J3mskWMIX6bY4m69g7z0svRc97hJgY7tDQrp8nggzG/C
// SIG // g8oArm5YxNJwn6E5SScxGqc65QjRdZQyFgLG3u0GjE8A
// SIG // PdjkPod/3w8K89m7v1k2aJdE2mG6jvdASgTpeehNrdiL
// SIG // S1cmcsoCXexhG84xghrXMIIa0wIBATBvMFsxCzAJBgNV
// SIG // BAYTAlVTMRgwFgYDVQQKEw8uTkVUIEZvdW5kYXRpb24x
// SIG // MjAwBgNVBAMTKS5ORVQgRm91bmRhdGlvbiBQcm9qZWN0
// SIG // cyBDb2RlIFNpZ25pbmcgQ0EyAhAOAvCsREVf/ih65u/I
// SIG // 72eiMA0GCWCGSAFlAwQCAQUAoIHAMBkGCSqGSIb3DQEJ
// SIG // AzEMBgorBgEEAYI3AgEEMBwGCisGAQQBgjcCAQsxDjAM
// SIG // BgorBgEEAYI3AgEVMC8GCSqGSIb3DQEJBDEiBCDa0jBp
// SIG // 9zZLVxWkz47V7sFI0ju92enXRDnsAcQadBOT3jBUBgor
// SIG // BgEEAYI3AgEMMUYwRKAggB4ARABvAGMAZgB4ACAAYwBv
// SIG // AGQAZQAgAHMAaQBnAG6hIIAeaHR0cHM6Ly9kb3RuZXQu
// SIG // Z2l0aHViLmlvL2RvY2Z4MA0GCSqGSIb3DQEBAQUABIIC
// SIG // AALhEqxaxjgCb5ScGhGhYeYF/dbI2j3rf4NCEaNMnBMD
// SIG // 7MBplNPc64zh+cV/NMpdvw9+BOwB+nXz9zmF3773mG2d
// SIG // 1YZRLT8+ewzLCu3T4z4hb7ivokv5xrPM6tvvqiiEn+Sd
// SIG // wEIE/VhgEaJMb94/ZbzrhW619nWRaslj4Wy/3qehtuLv
// SIG // 4I/zg3rr/aQFpw8dyqRaQ94hfbRAaOAmM6S2DfIka9zZ
// SIG // 80uCqCdH4l5+15sTXlwwO/5WXeRvIAK8DLlKhu0YuK0d
// SIG // eLSVfKXRutyIqaDd3s74r2/88uFqUcdjERd6TO64cNBM
// SIG // CPp8BqsWHjgpov1ICb/04WcSaqphDLill2SxxokKEE7C
// SIG // x3ISnFdM0D2IDcLmRjabnYbpctEgWLxTUiEOLzTBdpiX
// SIG // BGL5sWgGiZQilj7qS96wcpfkfXi9qfYG3t+F+WJvlRMC
// SIG // yZAIDxwT1kD/PWNSaZhq3HztobOs/sdYJDX5WqrBp4t8
// SIG // H09oSBpEqmgDRQajiwTm/dOS2EzY9KcUm0RmINkPQpMB
// SIG // G91zaJKPp/5V48x9i/979chJKqrwSD8NGG8Zo4H5gMAb
// SIG // 3q038bgyCJMhs39RT5vUC3eeom4PVtGNQ/vSg3IptX7j
// SIG // 0EHMMhZN5QkRQDTXRdUP8JmmsCS0VHTO20vOyueExvgl
// SIG // Txl/6dnBimEoRRrgTOBhJpuloYIXdjCCF3IGCisGAQQB
// SIG // gjcDAwExghdiMIIXXgYJKoZIhvcNAQcCoIIXTzCCF0sC
// SIG // AQMxDzANBglghkgBZQMEAgEFADB3BgsqhkiG9w0BCRAB
// SIG // BKBoBGYwZAIBAQYJYIZIAYb9bAcBMDEwDQYJYIZIAWUD
// SIG // BAIBBQAEIDVkusk/ViRi2v33T6mFgq9Rrf28SayqoRtw
// SIG // PUTmdWPMAhBrbjm969VPyH0XEVvsnC2EGA8yMDI2MDkx
// SIG // ODA5NTA0MlqgghM6MIIG7TCCBNWgAwIBAgIQCE/cM09+
// SIG // RU7bww+P+ZIYNTANBgkqhkiG9w0BAQsFADBpMQswCQYD
// SIG // VQQGEwJVUzEXMBUGA1UEChMORGlnaUNlcnQsIEluYy4x
// SIG // QTA/BgNVBAMTOERpZ2lDZXJ0IFRydXN0ZWQgRzQgVGlt
// SIG // ZVN0YW1waW5nIFJTQTQwOTYgU0hBMjU2IDIwMjUgQ0Ex
// SIG // MB4XDTI2MDgwNTAwMDAwMFoXDTM3MTEwNDIzNTk1OVow
// SIG // YzELMAkGA1UEBhMCVVMxFzAVBgNVBAoTDkRpZ2lDZXJ0
// SIG // LCBJbmMuMTswOQYDVQQDEzJEaWdpQ2VydCBTSEEyNTYg
// SIG // UlNBNDA5NiBUaW1lc3RhbXAgUmVzcG9uZGVyIDIwMjYg
// SIG // MTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIB
// SIG // ALZ7pvLJ/s1K+NSbTGWz/TjGMPh8CQ6RucZCLv5anHzW
// SIG // JjF/NWJrFIhy24fcpKXlgRiky4WAawDfU3YP0BMxt9l3
// SIG // Dm5oCG5Z69AqEN1kgHg2epx+l+lZBcmJCcN0ASURML5u
// SIG // FIS80sZsDwO3BSkUxDjLJhBI+qiZP3aixAC/qEGLjsBN
// SIG // lLol9VZ7pfGEXiMlneJIC5/YKuizVzNFKZZEeoy/0B8Z
// SIG // m+nzKBgSWG52lCO1w+nCg6XpCtklTJXeIg283hw7Tmms
// SIG // ZXR+SMbjbrEOvZ3fP2VxIgeR28Y90ZStd3F9VuA5RVyn
// SIG // b/whITPAo9b75Zr4Ta6Mj3URm26QZYMn/FnbuTegcoRc
// SIG // FEZ9FOqM5T6MTdtr/n74lIT/ug0eeOzmZ6QTFg33otX+
// SIG // bFRsIolvykE1jive4PuESaT8zzVeFWDAMDtozNgLctkG
// SIG // D1ZjkEyZtJrLl5ya0m5doH/ScpaZCZVl6pNUOCybMc/k
// SIG // xC6EAmSJY24L0yYKD1Nkddsnb/ItVKi/2nXpQNMu1PT5
// SIG // prW83vV8d67WowuUs0HdY4H8AMLGvdL/WHEj3ZnqMqAQ
// SIG // QP9u3Ai9t+5eQ02GDwy0ODjdzi0xlp70W+ow63/0++YD
// SIG // EX1M0iwgUHwbrJvfpklkZQvw3+kv3vUPItdwroczk9ic
// SIG // flf55W1zOEKAcJVAIXpcMCU9AgMBAAGjggGVMIIBkTAM
// SIG // BgNVHRMBAf8EAjAAMB0GA1UdDgQWBBQUyWOKMC7USvtu
// SIG // lPPm40B+9ezN4jAfBgNVHSMEGDAWgBTvb1NK6eQGfHrK
// SIG // 4pBW9i/USezLTjAOBgNVHQ8BAf8EBAMCB4AwFgYDVR0l
// SIG // AQH/BAwwCgYIKwYBBQUHAwgwgZUGCCsGAQUFBwEBBIGI
// SIG // MIGFMCQGCCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdp
// SIG // Y2VydC5jb20wXQYIKwYBBQUHMAKGUWh0dHA6Ly9jYWNl
// SIG // cnRzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRH
// SIG // NFRpbWVTdGFtcGluZ1JTQTQwOTZTSEEyNTYyMDI1Q0Ex
// SIG // LmNydDBfBgNVHR8EWDBWMFSgUqBQhk5odHRwOi8vY3Js
// SIG // My5kaWdpY2VydC5jb20vRGlnaUNlcnRUcnVzdGVkRzRU
// SIG // aW1lU3RhbXBpbmdSU0E0MDk2U0hBMjU2MjAyNUNBMS5j
// SIG // cmwwIAYDVR0gBBkwFzAIBgZngQwBBAIwCwYJYIZIAYb9
// SIG // bAcBMA0GCSqGSIb3DQEBCwUAA4ICAQCNxTphHp1SCt+Z
// SIG // rAmAfn0oQLFr0mLywSLaDXQIENoyKqxrFbJblzCVP/pk
// SIG // XmwXOdrOpWygLzlT12os5ipDCy35RBCg2UMeApEtrfGh
// SIG // z45F4Wt4WGdNdIbRWt3YTYJmpR+b7lr4d7Uwn+H600u4
// SIG // D7RnOGf8Wj4UNgAdZkfHhHv1mx9EVh71SJelcEN/oORS
// SIG // jXzdjfw1iZH9d8Nh/thn6hH23d+VsPAr6GAYyzSA02nX
// SIG // D1nYLI7Ijmiv+xLCiYC41DSFYL3GhTiy0PxpawPtGRya
// SIG // BVGzq+UiTfM8pD7KVyF5aQyWP4KhVGUUTnmm/RlYJoW3
// SIG // TiXA/+t0YcT2oRVBm3JETjajHug2AL+v5jhtKVnd3D0r
// SIG // bHXEu27o+Q8p4sEWPMqKDB+qbceb6T/6WcwTwXmQ9lOC
// SIG // LLYcsQeSWmvKqzpAec9etE14jOQAzLKWdE3w/TCaKtLR
// SIG // aRT7LCkRYVnhA2D73FLje1O5b3HR5eHs0NzU/+xX7NbE
// SIG // dcofy0W3Wdwd1XOqtlpg/JgwtKfZM5dqO94lbUveOiJB
// SIG // I+xZEbGRsMNbXmMREUTgu+Oca7Y73MPWcslIx2VhkSKS
// SIG // XjDbD6rgg39H5Mh7QfieAIjWagkJNt68Yfim6cjEzVSi
// SIG // LSeZfdkr5dtFPTW6jATlWJdYeeDRGCyatf8R1hSjzSvd
// SIG // N8yWQPT9gzCCBrQwggScoAMCAQICEA3HrFcF/yGZLkBD
// SIG // Igw6SYYwDQYJKoZIhvcNAQELBQAwYjELMAkGA1UEBhMC
// SIG // VVMxFTATBgNVBAoTDERpZ2lDZXJ0IEluYzEZMBcGA1UE
// SIG // CxMQd3d3LmRpZ2ljZXJ0LmNvbTEhMB8GA1UEAxMYRGln
// SIG // aUNlcnQgVHJ1c3RlZCBSb290IEc0MB4XDTI1MDUwNzAw
// SIG // MDAwMFoXDTM4MDExNDIzNTk1OVowaTELMAkGA1UEBhMC
// SIG // VVMxFzAVBgNVBAoTDkRpZ2lDZXJ0LCBJbmMuMUEwPwYD
// SIG // VQQDEzhEaWdpQ2VydCBUcnVzdGVkIEc0IFRpbWVTdGFt
// SIG // cGluZyBSU0E0MDk2IFNIQTI1NiAyMDI1IENBMTCCAiIw
// SIG // DQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBALR4MdMK
// SIG // mEFyvjxGwBysddujRmh0tFEXnU2tjQ2UtZmWgyxU7UNq
// SIG // EY81FzJsQqr5G7A6c+Gh/qm8Xi4aPCOo2N8S9SLrC6Kb
// SIG // ltqn7SWCWgzbNfiR+2fkHUiljNOqnIVD/gG3SYDEAd4d
// SIG // g2dDGpeZGKe+42DFUF0mR/vtLa4+gKPsYfwEu7EEbkC9
// SIG // +0F2w4QJLVSTEG8yAR2CQWIM1iI5PHg62IVwxKSpO0Xa
// SIG // F9DPfNBKS7Zazch8NF5vp7eaZ2CVNxpqumzTCNSOxm+S
// SIG // AWSuIr21Qomb+zzQWKhxKTVVgtmUPAW35xUUFREmDrMx
// SIG // SNlr/NsJyUXzdtFUUt4aS4CEeIY8y9IaaGBpPNXKFifi
// SIG // nT7zL2gdFpBP9qh8SdLnEut/GcalNeJQ55IuwnKCgs+n
// SIG // rpuQNfVmUB5KlCX3ZA4x5HHKS+rqBvKWxdCyQEEGcbLe
// SIG // 1b8Aw4wJkhU1JrPsFfxW1gaou30yZ46t4Y9F20HHfIY4
// SIG // /6vHespYMQmUiote8ladjS/nJ0+k6MvqzfpzPDOy5y6g
// SIG // qztiT96Fv/9bH7mQyogxG9QEPHrPV6/7umw052AkyiLA
// SIG // 6tQbZl1KhBtTasySkuJDpsZGKdlsjg4u70EwgWbVRSX1
// SIG // Wd4+zoFpp4Ra+MlKM2baoD6x0VR4RjSpWM8o5a6D8bpf
// SIG // m4CLKczsG7ZrIGNTAgMBAAGjggFdMIIBWTASBgNVHRMB
// SIG // Af8ECDAGAQH/AgEAMB0GA1UdDgQWBBTvb1NK6eQGfHrK
// SIG // 4pBW9i/USezLTjAfBgNVHSMEGDAWgBTs1+OC0nFdZEzf
// SIG // Lmc/57qYrhwPTzAOBgNVHQ8BAf8EBAMCAYYwEwYDVR0l
// SIG // BAwwCgYIKwYBBQUHAwgwdwYIKwYBBQUHAQEEazBpMCQG
// SIG // CCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdpY2VydC5j
// SIG // b20wQQYIKwYBBQUHMAKGNWh0dHA6Ly9jYWNlcnRzLmRp
// SIG // Z2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRSb290RzQu
// SIG // Y3J0MEMGA1UdHwQ8MDowOKA2oDSGMmh0dHA6Ly9jcmwz
// SIG // LmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRSb290
// SIG // RzQuY3JsMCAGA1UdIAQZMBcwCAYGZ4EMAQQCMAsGCWCG
// SIG // SAGG/WwHATANBgkqhkiG9w0BAQsFAAOCAgEAF877FoAc
// SIG // /gc9EXZxML2+C8i1NKZ/zdCHxYgaMH9Pw5tcBnPw6O6F
// SIG // TGNpoV2V4wzSUGvI9NAzaoQk97frPBtIj+ZLzdp+yXdh
// SIG // OP4hCFATuNT+ReOPK0mCefSG+tXqGpYZ3essBS3q8nL2
// SIG // UwM+NMvEuBd/2vmdYxDCvwzJv2sRUoKEfJ+nN57mQfQX
// SIG // wcAEGCvRR2qKtntujB71WPYAgwPyWLKu6RnaID/B0ba2
// SIG // H3LUiwDRAXx1Neq9ydOal95CHfmTnM4I+ZI2rVQfjXQA
// SIG // 1WSjjf4J2a7jLzWGNqNX+DF0SQzHU0pTi4dBwp9nEC8E
// SIG // AqoxW6q17r0z0noDjs6+BFo+z7bKSBwZXTRNivYuve3L
// SIG // 2oiKNqetRHdqfMTCW/NmKLJ9M+MtucVGyOxiDf06VXxy
// SIG // KkOirv6o02OoXN4bFzK0vlNMsvhlqgF2puE6FndlENSm
// SIG // E+9JGYxOGLS/D284NHNboDGcmWXfwXRy4kbu4QFhOm0x
// SIG // JuF2EZAOk5eCkhSxZON3rGlHqhpB/8MluDezooIs8CVn
// SIG // rpHMiD2wL40mm53+/j7tFaxYKIqL0Q4ssd8xHZnIn/7G
// SIG // ELH3IdvG2XlM9q7WP/UwgOkw/HQtyRN62JK4S1C8uw3P
// SIG // dBunvAZapsiI5YKdvlarEvf8EA+8hcpSM9LHJmyrxaFt
// SIG // oza2zNaQ9k+5t1wwggWNMIIEdaADAgECAhAOmxiO+dAt
// SIG // 5+/bUOIIQBhaMA0GCSqGSIb3DQEBDAUAMGUxCzAJBgNV
// SIG // BAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAX
// SIG // BgNVBAsTEHd3dy5kaWdpY2VydC5jb20xJDAiBgNVBAMT
// SIG // G0RpZ2lDZXJ0IEFzc3VyZWQgSUQgUm9vdCBDQTAeFw0y
// SIG // MjA4MDEwMDAwMDBaFw0zMTExMDkyMzU5NTlaMGIxCzAJ
// SIG // BgNVBAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMx
// SIG // GTAXBgNVBAsTEHd3dy5kaWdpY2VydC5jb20xITAfBgNV
// SIG // BAMTGERpZ2lDZXJ0IFRydXN0ZWQgUm9vdCBHNDCCAiIw
// SIG // DQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAL/mkHNo
// SIG // 3rvkXUo8MCIwaTPswqclLskhPfKK2FnC4SmnPVirdprN
// SIG // rnsbhA3EMB/zG6Q4FutWxpdtHauyefLKEdLkX9YFPFIP
// SIG // Uh/GnhWlfr6fqVcWWVVyr2iTcMKyunWZanMylNEQRBAu
// SIG // 34LzB4TmdDttceItDBvuINXJIB1jKS3O7F5OyJP4IWGb
// SIG // NOsFxl7sWxq868nPzaw0QF+xembud8hIqGZXV59UWI4M
// SIG // K7dPpzDZVu7Ke13jrclPXuU15zHL2pNe3I6PgNq2kZhA
// SIG // kHnDeMe2scS1ahg4AxCN2NQ3pC4FfYj1gj4QkXCrVYJB
// SIG // MtfbBHMqbpEBfCFM1LyuGwN1XXhm2ToxRJozQL8I11pJ
// SIG // pMLmqaBn3aQnvKFPObURWBf3JFxGj2T3wWmIdph2PVld
// SIG // QnaHiZdpekjw4KISG2aadMreSx7nDmOu5tTvkpI6nj3c
// SIG // AORFJYm2mkQZK37AlLTSYW3rM9nF30sEAMx9HJXDj/ch
// SIG // srIRt7t/8tWMcCxBYKqxYxhElRp2Yn72gLD76GSmM9GJ
// SIG // B+G9t+ZDpBi4pncB4Q+UDCEdslQpJYls5Q5SUUd0vias
// SIG // tkF13nqsX40/ybzTQRESW+UQUOsxxcpyFiIJ33xMdT9j
// SIG // 7CFfxCBRa2+xq4aLT8LWRV+dIPyhHsXAj6KxfgommfXk
// SIG // aS+YHS312amyHeUbAgMBAAGjggE6MIIBNjAPBgNVHRMB
// SIG // Af8EBTADAQH/MB0GA1UdDgQWBBTs1+OC0nFdZEzfLmc/
// SIG // 57qYrhwPTzAfBgNVHSMEGDAWgBRF66Kv9JLLgjEtUYun
// SIG // pyGd823IDzAOBgNVHQ8BAf8EBAMCAYYweQYIKwYBBQUH
// SIG // AQEEbTBrMCQGCCsGAQUFBzABhhhodHRwOi8vb2NzcC5k
// SIG // aWdpY2VydC5jb20wQwYIKwYBBQUHMAKGN2h0dHA6Ly9j
// SIG // YWNlcnRzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydEFzc3Vy
// SIG // ZWRJRFJvb3RDQS5jcnQwRQYDVR0fBD4wPDA6oDigNoY0
// SIG // aHR0cDovL2NybDMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0
// SIG // QXNzdXJlZElEUm9vdENBLmNybDARBgNVHSAECjAIMAYG
// SIG // BFUdIAAwDQYJKoZIhvcNAQEMBQADggEBAHCgv0NcVec4
// SIG // X6CjdBs9thbX979XB72arKGHLOyFXqkauyL4hxppVCLt
// SIG // pIh3bb0aFPQTSnovLbc47/T/gLn4offyct4kvFIDyE7Q
// SIG // Kt76LVbP+fT3rDB6mouyXtTP0UNEm0Mh65ZyoUi0mcud
// SIG // T6cGAxN3J0TU53/oWajwvy8LpunyNDzs9wPHh6jSTEAZ
// SIG // NUZqaVSwuKFWjuyk1T3osdz9HNj0d1pcVIxv76FQPfx2
// SIG // CWiEn2/K2yCNNWAcAgPLILCsWKAOQGPFmCLBsln1VWvP
// SIG // J6tsds5vIy30fnFqI2si/xK4VC0nftg62fC2h5b9W9Fc
// SIG // rBjDTZ9ztwGpn1eqXijiuZQxggN8MIIDeAIBATB9MGkx
// SIG // CzAJBgNVBAYTAlVTMRcwFQYDVQQKEw5EaWdpQ2VydCwg
// SIG // SW5jLjFBMD8GA1UEAxM4RGlnaUNlcnQgVHJ1c3RlZCBH
// SIG // NCBUaW1lU3RhbXBpbmcgUlNBNDA5NiBTSEEyNTYgMjAy
// SIG // NSBDQTECEAhP3DNPfkVO28MPj/mSGDUwDQYJYIZIAWUD
// SIG // BAIBBQCggdEwGgYJKoZIhvcNAQkDMQ0GCyqGSIb3DQEJ
// SIG // EAEEMBwGCSqGSIb3DQEJBTEPFw0yNjA5MTgwOTUwNDJa
// SIG // MCsGCyqGSIb3DQEJEAIMMRwwGjAYMBYEFFHZq9oDSXPY
// SIG // T0JmrKSCSOazacQ5MC8GCSqGSIb3DQEJBDEiBCA9d/P4
// SIG // f4zKSwCAR/obrmyxp3PLPpjUd+JMceGavBrUNDA3Bgsq
// SIG // hkiG9w0BCRACLzEoMCYwJDAiBCAtoJ2n9BMfn+cttsXm
// SIG // 6cllZ1WvBD8ep0LMDSEg4UHr/DANBgkqhkiG9w0BAQEF
// SIG // AASCAgCYSxNHcO3L+iMozfJnxx9QFjVgz83LbRooWBXv
// SIG // Z+AzWVnlTpVWRbSNNf7qDjwrhEUsCbY2loz9TsLWiBvk
// SIG // qpRoLwR7slVyoGH9MVpJ6ErW2mr3blF5EmaHXxEHHgiL
// SIG // lhHd4nbidN/2kotjwuclryFtMXj2jt5P11ZHgqgxvTfl
// SIG // VJDk41OXfvLlLHa+xz46AxkWFA9Qc2JT1FBcp0u6Yfb9
// SIG // GJr3YQF/X77at9RpaU5FhauCN/Xm+iWOXFjvYdD1efnD
// SIG // bJJSklEtl4NGIjqNYeF7FbSoxz/A2jbRvm8iURhX3N8a
// SIG // FkYfx5Xo8QASHs/9Zu13w16ZbVCN7K8c8JUUO9n013+E
// SIG // iHKTWn1BRHw+9thgl5Ix+gnhAiwzwfT8M4stEZgflfEX
// SIG // eJwc/rzboX9u2uTispMl+p6khvWkXL7DF2fMZDVx4w9c
// SIG // xX3TnjZf1ScJv61IOH1QXUdXu0yOAO4ikybDcEbam2NO
// SIG // 0KW9fLeZPDlbKrpo61rdzHf9XjdQCzSc/mLUgNnXcHmj
// SIG // 4SfvExhXkwtno7g6jwRzsEuBj3ua3Jhs/68nnzEGy5/d
// SIG // lQln3oDyVETJPFryDIfUpZ4JktPC9J051FWyQjl+bif/
// SIG // U014EpwBVXUjEnULHKz7h1QHAXeXA7IG1Gpc4OFQ6UQW
// SIG // 0lFx89z9UZ+HJjA+Y1Q6m4J0DrJqbg==
// SIG // End signature block
