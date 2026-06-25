// Initials fallback for an author avatar when there's no picture URL. The server
// sends only createdByName + createdByPictureUrl (User has no color field), so the
// island derives initials client-side (mirrors User.Initials: first + last word).
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}
