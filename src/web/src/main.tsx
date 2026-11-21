const savedTheme = localStorage.getItem('rp.theme')
if (savedTheme === 'light' || savedTheme === 'dark') document.documentElement.dataset.theme = savedTheme

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './styles.css'

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
