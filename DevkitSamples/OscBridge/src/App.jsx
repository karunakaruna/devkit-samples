import { useState, useEffect } from 'react'
import useWebSocket from 'react-use-websocket'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import CssBaseline from '@mui/material/CssBaseline'
import Box from '@mui/material/Box'
import Paper from '@mui/material/Paper'
import Typography from '@mui/material/Typography'
import List from '@mui/material/List'
import ListItem from '@mui/material/ListItem'
import ListItemText from '@mui/material/ListItemText'
import Chip from '@mui/material/Chip'

const darkTheme = createTheme({
  palette: {
    mode: 'dark',
  },
})

const formatArgs = (args) => {
  return args.map(arg => 
    typeof arg === 'number' ? arg.toFixed(3) : arg
  ).join(', ')
}

const getMessageColor = (address) => {
  if (address.includes('led')) return 'success'
  if (address.includes('vibration')) return 'warning'
  if (address.includes('device')) return 'info'
  return 'default'
}

function App() {
  const [messages, setMessages] = useState([])
  const { lastJsonMessage } = useWebSocket('ws://localhost:3000/ws', {
    onMessage: () => {
      if (lastJsonMessage) {
        setMessages(prev => [...prev, lastJsonMessage].slice(-100))
      }
    },
  })

  return (
    <ThemeProvider theme={darkTheme}>
      <CssBaseline />
      <Box sx={{ p: 3, height: '100vh', overflow: 'hidden' }}>
        <Typography variant="h4" gutterBottom>
          OSC Bridge Monitor
        </Typography>
        <Paper 
          sx={{ 
            height: 'calc(100vh - 100px)', 
            overflow: 'auto',
            bgcolor: 'background.paper' 
          }}
        >
          <List>
            {messages.map((msg, index) => (
              <ListItem key={index} divider>
                <ListItemText
                  primary={
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                      <Chip 
                        label={msg.address} 
                        color={getMessageColor(msg.address)}
                        size="small"
                      />
                      <Typography variant="body2" color="text.secondary">
                        {formatArgs(msg.args)}
                      </Typography>
                    </Box>
                  }
                  secondary={new Date(msg.timestamp).toLocaleTimeString()}
                />
              </ListItem>
            ))}
          </List>
        </Paper>
      </Box>
    </ThemeProvider>
  )
}

export default App
