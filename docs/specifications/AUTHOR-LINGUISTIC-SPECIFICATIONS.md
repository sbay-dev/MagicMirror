# AUTHOR LINGUISTIC SPECIFICATIONS — Magic Mirror / المرآة السحرية

> This document records the author's (user's) requests **verbatim** (the linguistic
> specification). Each specification is translated into executable, code-linked directives in
> [`directives/SPEC-0001-AI-DIRECTIVES.md`](directives/SPEC-0001-AI-DIRECTIVES.md).

---

## SPEC-AUTH-001 — Initial product specification (verbatim, Arabic)

> اتصل بخادم mcp من على الصفحه https://wmr-doc.pages.dev/ واستخدم WasmMVCRuntime لانشاء تطبيق
> MAUI يعمل عبر الذكاء الصناعي كما في توثيق mcp عبر gpt-oss120b عبركلاودفلير ودوره هو كالمراة
> السحرية عباره عن نافذه زجاجيه شفافه يمكن سحبها فوق اي نافذه مفتوحه تقوم هذه النافذه بالتقاط
> النص المعروض عبر تقنيات ocr متقدمه مثل التي في X:\source\THEORYS ويقوم بترجمة المحتوى النصي
> الذي خلف المرآة للغة الرئيسية التي يتم تحديدها عبر اعدادات التطبيق ومن خلال تعتيم طفيف
> والتقاط لنوع الخط وحجمه كما تفعل ادوات مشروع X:\source\THEORYS يتم مطابقة النص المترجم فوق
> النص الاصلي بدون ان يتاثر المنظور بشكل كبير , هذا المشروع سيعمل عبر طبقة ويب من اجل سهولة
> توفير الخطوط والربط مع chatgpt-oss120b وطبقه اصيله تلتقط محتويات نوافذ الانظمه مثل ويندوز او
> اندرويد مبدئيا وتهيء للتوسع الى انظمه اضافية .

**English summary (non-normative):** Connect to the MCP server at https://wmr-doc.pages.dev/ and
use WasmMvcRuntime to build a MAUI app powered by AI (gpt-oss-120b via Cloudflare, as per the MCP
docs). It is a "magic mirror": a transparent glass window draggable over any open window that
captures the on-screen text via advanced OCR (like the tools in `X:\source\THEORYS`), translates
the text behind the mirror into the main language set in the app settings, and — through slight
dimming and by capturing the font type and size — overlays the translated text on top of the
original without significantly disturbing the perspective. It runs as a **web layer** (to easily
provide fonts and connect with chatgpt-oss-120b) and a **native layer** that captures system
window contents (Windows or Android initially), prepared to expand to more systems.

### Confirmed design decisions (author elicitation)
- Primary platform: **Windows first**; Android scaffolded for later.
- OCR: **Tesseract (THEORYS eng+ara) primary + native OS OCR fallback**.
- Default main (target) language: **Arabic (ar)**, user-configurable.
- AI path: **web-layer gateway primary + direct Sarmad mesh fallback**.
- Scaffolding: **`cepha native-app`** (premium Native App Builder — trial).

---

## SPEC-AUTH-002 — Crash on Translate (verbatim, Arabic)

> انهار التطبيق عند النقر على زر ترجم

**English:** The app crashed when clicking the Translate button.

---

## SPEC-AUTH-003 — Translation scrambles text order (verbatim, Arabic)

> الترجمه تشوه ترتيب النص

**English:** The translation distorts/scrambles the order of the text.

---

## SPEC-AUTH-004 — Translated-text size & arrangement controls (verbatim, Arabic)

> النافذه السحرية يجب ان تزود بمفاهيم التحكم في حجم وترتيب النصوص المترجمه بحسب حجم النافذه اي
> تحسين ui

**English:** The magic window must be provided with concepts for controlling the **size** and
**arrangement** of the translated texts according to the window size — i.e. a UI improvement.

---

## SPEC-AUTH-005 — Faster live capture + read text without OCR (verbatim, Arabic)

> الاحظ بطئ في المراة السحريه عندما نسحبها تشر ان الالتقاط يستغرق وقت ضئيل ويجب زاده سرعة
> الكابشره المباشر لتحسين تجربة المستخدم وتجنيبه الشعور ان التطبيق يسجل صورة الشاشه فاذا كان
> بالامكان التقاط النصوص دون ocr اذا اتاحتها النوافذ فهذا سيوفر علينا مواردنا

**English:** The mirror feels slow when dragged — the capture takes a moment; the live capture
must be faster to improve UX and avoid the feeling that the app is recording the screen. And if it
is possible to capture text **without OCR** when the windows expose it, that would save resources.

---

## SPEC-AUTH-006 — Translation broke + futuristic UI redesign (verbatim, Arabic)

> التطبيق فقد القدره على الترجمه! ويحتاج الى تحسين الواجهات وتصميم ui بجعله تصميم قادم من المستقبل
> بمؤثرات مذهله

**English:** The app lost the ability to translate! And it needs UI/interface improvement, making
it a design **from the future** with **stunning effects**.

---

## SPEC-AUTH-007 — Copy original and translated text (verbatim, Arabic)

> ايضا وفر القدره على النسخ للنص الاصلي والمترجم

**English:** Also provide the ability to copy the original text and the translated text.

---

## SPEC-AUTH-008 — Domain-aware highest-quality translation (verbatim, Arabic)

> تاكد ان النموذج المستخدم gptoss120 لديه التعليمات التي تجعله يقدم ترجمة علمية لمستوى التعليم العالي
> وترجمه شرعية للنصوص الدينية وفحص طبيعة النص وانتاج ترجمه باعلى مستوى على الاطلاق

**English:** Ensure that the used gpt-oss-120b model has instructions that make it produce
higher-education-level scientific translation, faithful/reverent translation for religious texts,
inspect the nature/domain of the text, and produce the highest possible translation quality.

---

## SPEC-AUTH-009 — Preserve font and direction in mixed Arabic/English output lines (verbatim, Arabic)

> بقي ان الخط واتجاه النص يجب ان يكونا ملتزمين ببناء سطر المخرجات لان هنا تداخل عند النصوص
> الانكليزيه يكسر السطر مثل cns

**English:** The remaining issue is that the font and text direction must respect the construction
of the output line, because English text such as `cns` overlaps/breaks the line.

---

## SPEC-AUTH-010 — Cultural-exchange lens and full-window manipulation (verbatim, Arabic)

> الهدف هو ان نتمكن عبر المراة السحرية من توفير اداة تبادل الثقافات

> خذ لقطة شاشة الان لترى انني غير قادر على القراءه لان تباعد الاسطر يمنع ذلك وتتوسع المشكله مع
> التكبير لان معدل التباعد بين الاسطر لا يعتبر ديناميكيا ولا يراعي منطق الانتقال الى ثقافه اخرى
> واعدادات متحكم بها , كما اود ان اضيف قدرة السحب والافلات من كافةالنافذه ولا تقتصر على الشريط
> العلوي لتسهيل التحكم

**English:** The goal is for the Magic Mirror to provide a cultural-exchange tool. The current
screenshot shows the text is unreadable because line spacing prevents reading, and the issue grows
when zooming because line spacing is not dynamic and does not respect movement into another culture
or user-controlled settings. Also add drag-and-drop from the entire window, not only the top toolbar,
to make control easier.

---

## SPEC-AUTH-011 — Preserve document layout, typography, and historical formatting cues (verbatim, Arabic)

> ما اتمنى الوصول له هو ان يكون لدي مستند مترجم بنفس تخطيط المستند الاصلي وبخط متغير بحسب طبيعة
> خط المستند الاصلي باللغه التي يحددها المستخدم وهو المعنى الصحيح لتبادل الثقافات وليس الترجمات
> لان كتب النظريات والصحافة كانت تعتمد تنسيقات تتغير مع الزمن ويمكن من تنسيق ورقة ما خصوصا الاوراق
> العلمية والكتب ان نعرف الحقبه التي نشأت فيها وهذا ما يجعلني افكر في تتبع التنسيقات .

**English:** The desired outcome is a translated document with the same layout as the original
document, using a variable target-language font according to the nature of the original document's
font. This is the correct meaning of cultural exchange, not just translation, because theory books
and journalism used formats that changed over time; from the formatting of a page, especially
scientific papers and books, one can infer the era in which it emerged. This motivates tracking
formats.

---

## SPEC-AUTH-012 — Mixed English runs and scroll overflow (verbatim, Arabic)

> مازالة النصوص الانكليزية تكسر السطر العربي وايضا عند التكبير لا يمكن التمرير للنصوص التي تدخل
> اسفل الاطار

**English:** English text still breaks the Arabic line, and when zooming it is not possible to
scroll to texts that go below the frame.

---

## SPEC-AUTH-013 — Transparent body while dragging (verbatim, Arabic)

> عند السحب والافلات اجعل جسم النافذه شفاف كي تتحسن تجربة المستخدم ويظهر الالتقاط كانه مباشر ,
> اليس ذلك افضل من تقليل الجوده او تثبيت الصوره

**English:** During drag-and-drop, make the window body transparent to improve UX and make capture
feel direct/live; this is better than reducing quality or freezing the image.

---

## SPEC-AUTH-014 — Document/book typography for translated text (verbatim, Arabic)

> تنسيق النص المترجم سيء للغاية ولا يمثل وثيقه او كنص كتاب انه متفاوت الحجم ويرتفع ويتاثر بشكل
> مزري مع تعدد اللغات

**English:** The translated text formatting is very poor and does not look like a document or book
text. It has inconsistent sizes, shifts vertically, and degrades badly with multilingual content.

---

## SPEC-AUTH-015 — Mouse wheel, double-click toggle, and mixed-language spacing (verbatim, Arabic)

> اضف تمرير بالفاره وكذلك نقرتين للتبديل بين الوضع الحي والترجمه وذلك لتحسين تجربة المستخدم ,
> كما انني لاحظت ان التقهقر في النص يحدث مع الحروف الانكليزية بحيث تحسب مساحه فارغه في السطر
> كبيره عند ادراج نص بلغه مختلفه

**English:** Add mouse-wheel scrolling and double-click/tap to switch between live mode and
translation mode to improve UX. Also, mixed English letters cause a text regression where a large
blank space is computed in the line when text from another language is inserted.

---

## SPEC-AUTH-016 — Keep Latin terms attached and remove oversized text shading (verbatim, Arabic)

> في النص "يستخدم المشغل المقترح إحداثيات CNS." جاءت cns اول السطر!

> كما ان التظليل الخلفي لمساحات النص اظهر العله لكبر حجمه الذي يغطي على البقية كما في النافذه
> المعروضه الحالية

**English:** In the text "the proposed operator uses CNS coordinates", `CNS` appeared at the start
of a line. Also, the background shading behind text spaces exposed the problem: its size is too
large and covers the rest, as shown in the currently displayed window.

---

## SPEC-AUTH-017 — Configurable overlay background and font colors (verbatim, Arabic)

> ينقصنا الان فقط ان نضيف امكانية تعديل لون وشفافيةالخلفية ولون الخط من المرآه نفسها ومن
> الاعدادات الافتراضيه ايضا

**English:** What remains is to add the ability to modify the background color, background opacity,
and font color from the mirror itself and also from the default settings.

---

## SPEC-AUTH-018 — Document direction, opaque reader page, and dictionary context menu (verbatim, Arabic)

> مازال النص الانكليزي يكسر تراتب السطر ويغير بدايته الصحيحه وكذلك حاوية الازرار الجديده تقص
> الجزئ السفلي من الازرار وان اضافة زر التحكم في الشفافية عند زيادة الكثافه الى اعلى نسبة يفترض
> ان تكون كامل الخلفية باللون المحدد اي تصبح ورقة اخرى بلغة القارئ مع التاكد من ان التنسيقات
> مضبوطه بحسب معايير الوثائق النصية وكما لو كان مستند وورد او pdf لانني الاحظ ان ترجمة العناوين
> تركب فوق العنوان في اليسار بينما النصوص ليست كذلكوالحل المثالي لهذا هو ان يعمم اتجاه النص بغض
> النظر عن الموضع الاصلي واضافة امكانية التضليل لاي كلمه او نص فيتم تضليل النص الاصلي في الوثيقه
> واضافة خيارات عند النقر بالزر الايمن مثل الترجمه المعجمية بالتالي يكون نموذج gptoss120 بليون
> من سياق الطلب السابق يقوم بتفصيل الترجمه معجميا وتوفير بدائل بحسب تصنيف النص الكامل بما يقطع
> الشك عند اختيار هذا الخيار الذي لن يقوم المستخدم بالبحث الا اذا لاحظ عدم وضوح المصطلح مع بناء
> اوامر رصينه للنموذج عند تلقي هذا الطلب بان يوفر ما لا يقل عن خمسة احتمالات للنص او الكلمه التي
> جرى عليها هذا الخيار وملخص نهائي يقطع الشك ويقرب المعنى يوضح السياقات التي وفرها وبناها على
> اي اساس .

**English:** English text still breaks line hierarchy and changes the correct line start; the new
button container clips the lower part of buttons; when opacity is raised to the highest value the
entire background should become the selected color, like another page in the reader's language.
Formatting must follow textual-document standards as if it were a Word/PDF document. Title
translations overlap the source title on the left while body text does not; the ideal solution is to
generalize text direction regardless of original position. Add the ability to highlight any word or
text, shading the original text in the document, and add right-click options such as dictionary
translation. For that option, gpt-oss-120b should use the full request/document context to provide a
lexical breakdown, at least five alternatives, and a final summary that reduces doubt by explaining
which contexts were used and on what basis.

---

## SPEC-AUTH-019 — Selection priority, accurate text hit testing, and draggable dictionary panel (verbatim, Arabic)

> المشكل ان تحديد النص يتعارض مع السحب والافلات بالتالي يجب ان يتقدم التحديد على السحب والافلات
> بعد النقر على الزر الامن وانبثاق المعجم

> لا يتوفر اي قدره على التحديد عوضا عن ان احداثيات النقر لا تتوافق مع النص الذي تم النقر عليه
> وكذالك نافذة المعجم غير منسقه فيها المخرجات ويفضل ان تكون قابله للسحب والافلات هي ايضا لانه
> تغطي على النص المرغوب تحديده .

**English:** Text selection conflicts with drag/drop; after right-click and dictionary popup,
selection must take precedence over drag/drop. There is no usable selection, click coordinates do
not match the clicked text, dictionary output is poorly formatted, and the dictionary window should
also be draggable because it covers the text the user wants to select.

---

## SPEC-AUTH-020 — Text selection must be directly possible (verbatim, Arabic)

> التحديد غير ممكن

**English:** Selection is not possible; the mirror must provide a directly usable way to select
visible text.

---

## SPEC-AUTH-021 — Movable dictionary panel, Arabic selection mapping, and gateway acceptance (verbatim, Arabic)

> نافذة المعجم لا يمكن تغيير مكانها وايضا لا يمكن تحديد النص العربي المراد التحقق من موضعه
> المقايل على الوثيقه وايضا المعجم لا يعمل لكون البوابه لم تقبل الطلب

**English:** The dictionary window cannot be moved; the user also cannot select the Arabic text whose
corresponding position on the document should be checked; and the dictionary does not work because
the gateway did not accept the request.

---

## SPEC-AUTH-022 — Sarmad gateway root cause must be investigated (verbatim, Arabic)

> هذا يعني ان بوابه سرمد لا تعمل منذ البداية بالتالي يجب ان نحقق في المشكله الجذرية

**English:** This means the Sarmad gateway has not been working from the beginning; therefore the
root cause must be investigated.

---

## SPEC-AUTH-023 — Dedicated Cloudflare gateway for Magic Mirror (verbatim, Arabic)

> ارى اننا بحاجه الى انشاء بوابة مخصصة لهذا التطبيق ونشرها على كلاودفلير وربط التطبيق بها حتى نضمن النتائج

**English:** We need to create a dedicated gateway for this application, deploy it to Cloudflare,
and connect the app to it so results are guaranteed.

---

## SPEC-AUTH-024 — Translation source and high-quality dictionary UI (verbatim, Arabic)

> نجح بالفعل لكن يجب ان يعرض مصدر الترجمه وايضا تنسيق مخرجات المعجم متداخله ولا تمثل معايير ui العالية الجوده وكما ان النص الذي يعيده المعجم عربي يجب الالتزام باتجاه النص وكذلك الاتجاه بحسب اعدادات المستخدم للغة الواجهه ولغة الترجمه

**English:** It succeeded, but it must show the translation source. Dictionary output formatting is
overlapping and does not meet high-quality UI standards. Since the dictionary returns Arabic text, it
must respect text direction, and direction should follow the user's interface-language and
translation-language settings.

---

## SPEC-AUTH-025 — W3C-like dictionary direction, accurate word hit-testing, and sentence review (verbatim, Arabic)

> مازال معجم لا يتبع اتجاه النص وحجمه بمعايير W3C المعتمده وايضا التحديد لا يحدد الكلمة التي
> ننقر عليها بل يحدد في مكان اخر وبصعوبه يمكن ان نقتنص الكلمه ولا توجد طريق لتحديد جمله كي
> تراجع في المعجم!

**English:** The dictionary still does not follow text direction and sizing according to accepted
W3C-like standards. Selection does not select the word being clicked; it selects somewhere else and
the word is hard to capture. There is also no way to select a sentence for dictionary review.

---

## SPEC-AUTH-026 — Adopt dictionary proposals into translation and model-learning rules (verbatim, Arabic)

> اضف امكانية اضافة احد مقترحات المعجم الى الترجمه المعروضه وحفظها في قواعد النموذج كي يتعلم
> تلقائا من الانتقاءات التي يقوم بها المستخدمين ويحسن قدراته في المستقبل واجعل الحفظ ضمن الية
> يسهل على النموذج التطور من خلالها

**English:** Add the ability to apply one of the dictionary proposals to the displayed translation
and save it into model rules so the model learns automatically from users' selections and improves
in the future. Make the saving mechanism easy for the model to evolve through.

---

## SPEC-AUTH-027 — Single selection marker and progressive translation stream (verbatim, Arabic)

> اصبحت تتكرر مربعات التحديد لكل كلمه واحده مربعين او اكثر وايضا اقترح ان يتم عرض الترجمه
> كستريم مستمر يسلم النص تدريجيا لتجنب الانتظار الطويل

**English:** Selection boxes are now duplicated for each word, two or more boxes. Also, translation
should be shown as a continuous stream that delivers text gradually to avoid long waiting.

---

## SPEC-AUTH-028 — Character-by-character stream and single selection marker (verbatim, Arabic)

> انه التسليم مازال على دفعات وليس حرف تلو حرفومازال هناك مربعين تحديد لكل كلمه

**English:** Delivery is still in batches and not character by character, and there are still two
selection boxes for each word.

---

## SPEC-AUTH-029 — Link translated selection to original text (verbatim, Arabic)

> لماذا لا يتم تحديد النص الاصلي عند تحديد الترجمه !

**English:** Why is the original text not selected when the translation is selected?

---

## SPEC-AUTH-030 — Raise default captured image quality for OCR (verbatim, Arabic)

> ارفع جوده الصورة الملتقطه الافتراضيه كي يتمكن OCR من التقاط النص الصحيح بدقه

**English:** Raise the default captured-image quality so OCR can capture the correct text accurately.

---

## SPEC-AUTH-031 — Resize the mirror with wheel/pinch, and Ctrl+wheel in translation mode (verbatim, Arabic)

> في وضع المرآة عند التمرير بعجلة الماوس او السحب باصبعين للخارج ارى ان يتم توسيع النافذه عبر زياده حجمها بشكل متدرج مع التمرير لتحسين تجربه المستخدم وتسهيل الامكانيات

> يمكن اضافة تكبير وتصغير وضع الترجمه عند النقر على CTRL+ عجلة الماوس يتم تكبير النافذه ايضا في وضع الترجمه وفي اللمس يكون بنفس الطريقه للوضع السابق في المراه

**English:** In mirror mode, mouse-wheel scrolling or two-finger outward drag should gradually enlarge
the window to improve UX. In translation mode, `Ctrl + mouse wheel` should resize the window too, and
touch pinch should work the same way as in mirror mode.

---

## SPEC-AUTH-032 — Smooth resize must not hide the mirror or pass input through (verbatim, Arabic)

> يجب ان يكون التكبير والتصغير ناعم وما يحدث الان ان النافذه تختفي وتعود ما قد يمرر النافذه التي خلف المراه بشكل مزعج خصوصا عند التمرير بسرعه!

**English:** Zooming/resizing must be smooth. The current behavior makes the window disappear and
return, which can annoyingly pass input to the window behind the mirror, especially during fast
scrolling.

---

## SPEC-AUTH-033 — Full-quality drop capture and visual transparency for zoom/frame resize (verbatim, Arabic)

> نحن خفضنا جودة صورة الالتقاط عند الافلات وذلك قبل ان نعتمد مبدأ الشفافية عند السحب ونريد اعادة الجوده الكامله للالتقاط واضافه مبدأ شفافية السحب الى التكبير ايضا او تمديد الاطار

**English:** We lowered captured-image quality on release before adopting drag transparency. Restore
full capture quality on release, and add the drag-transparency principle to zooming or frame
extension/resizing too.

---

## SPEC-AUTH-034 — Independent expert council and academic publication readiness (verbatim, Arabic)

> استعي مجلس خبراء مستقلين لتحقيق في جودة العرض الدلالي والربط واساليب عرض الترجمه والالتزام
> بالتنسيقات العلميه والمعيارية لدور النشر ثم اصدر النسخه الاحترافية التي يجمع عليها الكل بانها
> جاهزة للنشر الاكاديمي وستعتمدها دور النشر كاداة ترجمه حقيقية

**English:** Convene an independent expert council to investigate semantic display quality, linking,
translation presentation methods, and adherence to scientific/publisher formatting standards, then
issue the professional version once everyone agrees it is ready for academic publishing and publisher
adoption as a real translation tool.

---

## SPEC-AUTH-035 — Text direction, line estimation, and knowledge-source trust (verbatim, Arabic)

> اتجاه النص في الترجمه معطوب وتقدير الاسطر فقد قدرته وكذلك تمييز الاصل المعرفي الذي بنية عليه
> الترجمه لم يعد موثوقا اضف لذلك ان تبرير الترجمه المختلطه غير مفند نحن نمتلك ذكاء يترجم ويفهم
> السياق لم لا تبنى على اساسه القرارات المعرفية

**English:** Translation text direction is broken, line estimation has lost reliability, the knowledge
source on which the translation is built is no longer trustworthy, and mixed-translation justification
is not refuted. Since we have intelligence that translates and understands context, knowledge decisions
should be based on it.

---

## SPEC-AUTH-036 — Real text editor properties in the reader (verbatim, Arabic)

> لا يمكن الوثوق بالمحتوى العلمي اذا لم يلتزم بالاصطفاف الصحيح كما في تااقطه السابقه اذ ان اي
> مصطلح انكليزي يقلب السطر ويغر معناه ويجب التحقق دائما من جهة القارئ او نسخ النص واستخدام محرر
> خارجي ! يجب ان تدمج خصائص محررات النصوص الحقيقية في القارئ

**English:** Scientific content cannot be trusted if alignment is wrong; any English term can flip the
line and change the meaning, forcing verification through the reader side or by copying into an
external editor. The reader must integrate real text-editor properties.

---

## SPEC-AUTH-037 — Reader typography, hidden bidi controls, and detachable Kindle/Jarir-like reader (verbatim, Arabic)

> نص الملمراه يرتجف والقارئ يظهر بخط ضخم يجب ان يتحكم به وكذلك لا يخفي رموز pid lri كما في اللقطه
> يجب ان تحسن واجهة القارئ لتضاهي قارئ كيندل وقارئ جرير مثلا ويمكن فصله عن المراه

**English:** Mirror text jitters, the reader appears with a huge font that must be controllable, and
PID/LRI-like bidi symbols should not be visible. Improve the reader UI to resemble Kindle/Jarir-style
readers and allow detaching it from the mirror.

---

## SPEC-AUTH-038 — Replace canvas translation blocks with a transparent editor (verbatim, Arabic)

> ان تدفق نص القارئ اكثر استقرار واكثر تحكم بالتالي لم لا نفكر باستبدال الترجمه على الكانفاس بمحرر
> بدون خلفيه يضمن لنا دقه في التحديد وادوات وخيارات للنص عند النقر على الزر الايمن بدل استخدام
> الكتل التي اثبتت فشلها مرارا

**English:** Reader text flow is more stable and controllable; consider replacing canvas translation
with a transparent editor that guarantees selection precision and text tools/options on right-click
instead of repeatedly failing blocks.

---

## SPEC-AUTH-039 — Detached reader controls and glossary/provenance linkage (verbatim, Arabic)

> عدل ايضا التحكم في كثافة الخلفية ولونها واجعل النقر على زر القارئ يفصله مباشره لاننا بالفعل
> لدينا قارئ رائع مع بعض التحسينات في الخط والتضيليل بان ينعكس على النص الاصلي والمعجم سنمتلك افضل
> معيار لا محاله

> اذ ان النافذه المفصوله يفترض ان تزود بازرار تحكم اكثر في الحجم واللون والتفاف النص

> بقي ان تربط النص المظلل بخيار عرض المعجم وكذلك تحديد المقابل له في الترجمه الاصليه وعدم تطبيق
> ترجمه مختلطه لان ذلك قد يفقد نموذج سرمد مسارات المعجم ويمكن اتباع مفهوم الاثبات المبني على شجرة
> ميركل الذي يربط المصطلحات بالوثائق الاصليه ويمكن تعين مهمة الاثبات الى نموذج سرمد موازي يكون
> مدقق مسؤول عن ربط القيم والمعجم الحرفي والمعجم بالجمله ويعطي مسارات يمكن تمثيلها على ocr الاصلي
> بدقه

**English:** Improve background density/color control and make the reader button detach directly. The
detached window should include more controls for size, color, and wrapping. Link highlighted text to
dictionary display, identify its counterpart in the original translation, and avoid mixed translation
because it can break Sarmad glossary paths. Use a Merkle-tree proof concept that links terms to
original documents, with Sarmad acting as an auditor for literal glossary, sentence glossary, and OCR
representable paths.

---

## SPEC-AUTH-040 — Dictionary tabs, app-language direction, and explicit fallback prompt (verbatim, Arabic)

> نافذة المعجم يفضل ان تقوم بتحسينها وتطبيق اتجاه السطر بحسب اللغه الاصلية للتطبيق وفصل التوثيقات
> التقنية البحته في تبويب ضمن نافذة المعجم وذلك لتجنب الارباك وتحسين الية الترجمه التي تخلط بين
> الترجمات بان يكون الحد الفاصل صارم وتوفير خيار اختياري عندما لا يتمكن سرمد من توفير الترجمه بان
> تنبثق نافذه مثلا تفيد بان يتم الانتظار او استخدام البديل بشكل صريح في كل مره يتم تبديل المصدر

**English:** Improve the dictionary window, apply line direction according to the app's original
language, separate purely technical documentation into a tab inside the dictionary window, and make the
translation source boundary strict. When Sarmad cannot provide a translation, show an optional prompt
to wait/retry or explicitly use the alternative each time the source is changed.

---

## Standing author conventions (from memory)
- Document data-extraction/processing functions thoroughly (parameters + protocol) so they can be
  reused to reproduce the same output quality.
- Record each author spec verbatim here, with a linked `SPEC-NNNN-AI-DIRECTIVES.md` translating it
  to executable, code-linked directives.
- Bandwidth is limited: do not download large assets without explicit approval; prefer local assets.
- Never terminate the user's `copilot.exe`/`node.exe` processes (AI model training).
