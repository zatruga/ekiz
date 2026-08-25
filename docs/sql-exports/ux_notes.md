## UI Pro Max Search Results
**Domain:** ux | **Query:** data table filtering status badges
**Source:** ux-guidelines.csv | **Found:** 5 results

### Result 1
- **Category:** Responsive
- **Issue:** Table Handling
- **Platform:** Web
- **Description:** Tables can overflow on mobile
- **Do:** Use horizontal scroll or card layout
- **Don't:** Wide tables breaking layout
- **Code Example Good:** overflow-x-auto wrapper
- **Code Example Bad:** Table overflows viewport
- **Severity:** Medium

### Result 2
- **Category:** Forms
- **Issue:** Submit Feedback
- **Platform:** All
- **Description:** Confirm form submission status
- **Do:** Show loading then success/error state
- **Don't:** No feedback after submit
- **Code Example Good:** Loading -> Success message
- **Code Example Bad:** Button click with no response
- **Severity:** High

### Result 3
- **Category:** Feedback
- **Issue:** Loading Indicators
- **Platform:** All
- **Description:** Show system status during waits
- **Do:** Show spinner/skeleton for operations > 300ms
- **Don't:** No feedback during loading
- **Code Example Good:** Skeleton or spinner
- **Code Example Bad:** Frozen UI
- **Severity:** High

### Result 4
- **Category:** Data Entry
- **Issue:** Bulk Actions
- **Platform:** Web
- **Description:** Editing one by one is tedious
- **Do:** Allow multi-select and bulk edit
- **Don't:** Single row actions only
- **Code Example Good:** Checkbox column + Action bar
- **Code Example Bad:** Repeated actions per row
- **Severity:** Low

### Result 5
- **Category:** Sustainability
- **Issue:** Auto-Play Video
- **Platform:** Web
- **Description:** Video consumes massive data and energy
- **Do:** Click-to-play or pause when off-screen
- **Don't:** Auto-play high-res video loops
- **Code Example Good:** playsInline muted preload='none'
- **Code Example Bad:** autoplay loop
- **Severity:** Medium

## UI Pro Max Search Results
**Domain:** chart | **Query:** KPI stat cards summary tiles
**Source:** charts.csv | **Found:** 1 results

### Result 1
- **Data Type:** Process Mining
- **Keywords:** process, mining, variants, path, bottleneck, log
- **Best Chart Type:** Process Map / Graph
- **Secondary Options:** Directed Acyclic Graph (DAG), Petri Net
- **Color Guidance:** Happy path: #10B981 (Thick). Deviations: #F59E0B (Thin). Bottlenecks: #EF4444.
- **Accessibility Notes:** ⚠ Complex graphs hard to navigate. Provide path summary.
- **Library Recommendation:** React-Flow, Cytoscape.js, Recharts
- **Interactive Level:** Drag + Node-Click

## UI Pro Max Stack Guidelines
**Stack:** html-tailwind | **Query:** layout form table
**Source:** stacks/html-tailwind.csv | **Found:** 8 results

### Result 1
- **Category:** Layout
- **Guideline:** Responsive padding
- **Description:** Adjust padding for different screen sizes
- **Do:** px-4 md:px-6 lg:px-8
- **Don't:** Same padding all sizes
- **Code Good:** px-4 sm:px-6 lg:px-8
- **Code Bad:** px-8 (same all sizes)
- **Severity:** Medium
- **Docs URL:** 

### Result 2
- **Category:** Typography
- **Guideline:** Text truncation
- **Description:** Handle long text gracefully
- **Do:** truncate or line-clamp-*
- **Don't:** Overflow breaking layout
- **Code Good:** line-clamp-2
- **Code Bad:** No overflow handling
- **Severity:** Medium
- **Docs URL:** https://tailwindcss.com/docs/text-overflow

### Result 3
- **Category:** Layout
- **Guideline:** Grid gaps
- **Description:** Use consistent gap utilities for spacing
- **Do:** gap-4 gap-6 gap-8
- **Don't:** Margins on individual items
- **Code Good:** grid gap-6
- **Code Bad:** grid with mb-4 on each item
- **Severity:** Medium
- **Docs URL:** https://tailwindcss.com/docs/gap

### Result 4
- **Category:** Layout
- **Guideline:** Flexbox alignment
- **Description:** Use flex utilities for alignment
- **Do:** items-center justify-between
- **Don't:** Multiple nested wrappers
- **Code Good:** flex items-center justify-between
- **Code Bad:** Nested divs for alignment
- **Severity:** Low
- **Docs URL:** 

### Result 5
- **Category:** Spacing
- **Guideline:** Negative margins
- **Description:** Use sparingly for overlapping effects
- **Do:** -mt-4 for overlapping elements
- **Don't:** Negative margins for layout fixing
- **Code Good:** -mt-8 for card overlap
- **Code Bad:** -m-2 to fix spacing issues
- **Severity:** Medium
- **Docs URL:** 

### Result 6
- **Category:** Layout
- **Guideline:** Use shrink-0 shorthand
- **Description:** Shorter class name for flex-shrink-0
- **Do:** shrink-0 shrink
- **Don't:** flex-shrink-0 flex-shrink
- **Code Good:** shrink-0
- **Code Bad:** flex-shrink-0
- **Severity:** Low
- **Docs URL:** https://tailwindcss.com/docs/flex-shrink

### Result 7
- **Category:** Layout
- **Guideline:** Container Queries
- **Description:** Use @container for component-based responsiveness
- **Do:** Use @container and @lg: etc.
- **Don't:** Media queries for component internals
- **Code Good:** @container @lg:grid-cols-2
- **Code Bad:** @media (min-width: ...) inside component
- **Severity:** Medium
- **Docs URL:** https://github.com/tailwindlabs/tailwindcss-container-queries

### Result 8
- **Category:** Layout
- **Guideline:** Use size-* for square dimensions
- **Description:** Single utility for equal width and height
- **Do:** size-4 size-8 size-12
- **Don't:** Separate h-* w-* for squares
- **Code Good:** size-6
- **Code Bad:** h-6 w-6
- **Severity:** Low
- **Docs URL:** https://tailwindcss.com/docs/size

