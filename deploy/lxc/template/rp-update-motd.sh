# Shown at every console / ssh login when the weekly check found a newer build.
if [ -s /var/lib/reflectopatronus/update-available ] && [ "$(id -u)" = 0 ]; then
  echo
  echo "  A newer ReflectoPatronus build is available ($(cat /var/lib/reflectopatronus/update-available))."
  echo "  Run  rp-update  to see what changed and update (it asks before doing anything)."
  echo
fi
