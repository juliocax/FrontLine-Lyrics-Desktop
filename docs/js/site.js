function flInitNav() {
  const nav = document.getElementById("mainNav");
  if (!nav) return;

  const pill = nav.querySelector(".nav-pill");
  const links = Array.from(nav.querySelectorAll(".nav-link"));
  const allNavTargets = Array.from(document.querySelectorAll("[data-nav]"));
  if (!pill || links.length === 0) return;

  function movePillTo(link) {
    pill.style.width = link.offsetWidth + "px";
    pill.style.transform = "translateX(" + link.offsetLeft + "px)";
    pill.style.opacity = "1";
  }

  function setActive(key) {
    let matched = null;
    allNavTargets.forEach((link) => {
      const isMatch = link.dataset.nav === key;
      link.classList.toggle("is-active", isMatch);
      if (isMatch && links.includes(link)) matched = link;
    });
    if (matched) movePillTo(matched);
  }

  links.forEach((link) => {
    link.addEventListener("click", () => setActive(link.dataset.nav));
  });

  window.addEventListener("load", () => {
    const current = links.find((l) => l.classList.contains("is-active"));
    if (current) movePillTo(current);
  });

  const initialKey = document.body.dataset.navActive || links[0].dataset.nav;
  requestAnimationFrame(() => setActive(initialKey));

  window.addEventListener("resize", () => {
    const current = links.find((l) => l.classList.contains("is-active"));
    if (current) movePillTo(current);
  });

  const spySections = ["hero", "recursos", "apoiar"]
    .map((id) => document.getElementById(id))
    .filter(Boolean);

  const sectionToKey = { hero: "home", recursos: "how", apoiar: "support" };

  if (spySections.length > 1 && "IntersectionObserver" in window) {
    const observer = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) setActive(sectionToKey[entry.target.id]);
        });
      },
      { rootMargin: "-40% 0px -50% 0px", threshold: 0 }
    );
    spySections.forEach((section) => observer.observe(section));
  }
}

document.addEventListener("DOMContentLoaded", flInitNav);


function flInitMobileMenu() {
  const toggle = document.getElementById("mobileMenuToggle");
  const menu = document.getElementById("mobileMenu");
  if (!toggle || !menu) return;

  function closeMenu() {
    toggle.classList.remove("is-open");
    menu.classList.remove("is-open");
    document.body.classList.remove("menu-open");
    toggle.setAttribute("aria-expanded", "false");
  }

  function openMenu() {
    toggle.classList.add("is-open");
    menu.classList.add("is-open");
    document.body.classList.add("menu-open");
    toggle.setAttribute("aria-expanded", "true");
  }

  toggle.addEventListener("click", () => {
    if (menu.classList.contains("is-open")) {
      closeMenu();
    } else {
      openMenu();
    }
  });

  menu.querySelectorAll("a").forEach((link) => {
    link.addEventListener("click", closeMenu);
  });

  window.addEventListener("resize", () => {
    if (window.innerWidth >= 1200) closeMenu();
  });
}

document.addEventListener("DOMContentLoaded", flInitMobileMenu);


function initHeroCarousel() {
  const carousel = document.getElementById("hero-carousel");
  if (!carousel) return;
  
  const images = carousel.querySelectorAll("img");
  if (images.length <= 1) return;

  let currentIndex = 0;
  
  setInterval(() => {
    images[currentIndex].classList.remove("opacity-100");
    images[currentIndex].classList.add("opacity-0");

    currentIndex = (currentIndex + 1) % images.length;

    images[currentIndex].classList.remove("opacity-0");
    images[currentIndex].classList.add("opacity-100");
  }, 7000);
}

document.addEventListener("DOMContentLoaded", initHeroCarousel);